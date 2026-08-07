using LibreHardwareMonitor.Hardware;
using ModernWigiDash.Service.Contracts;

namespace ModernWigiDash.Service.Services;

/// <summary>
/// Background worker that polls hardware sensors via LibreHardwareMonitorLib.
/// Runs inside the LocalSystem service so no user elevation is required.
/// Exposes the latest snapshot to WCF operations via <see cref="GetSnapshot"/>.
/// </summary>
public sealed class LhmSensorReader : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Computer _computer;
    private readonly ILogger<LhmSensorReader> _logger;
    private SensorSnapshotDto _latest = new();

    public LhmSensorReader(ILogger<LhmSensorReader> logger)
    {
        _logger = logger;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = true,
            IsStorageEnabled = true,
            IsBatteryEnabled = true,
            IsPowerMonitorEnabled = true,
            IsPsuEnabled = true
        };
    }

    /// <summary>
    /// Get the latest sensor snapshot. Safe to call from multiple threads.
    /// </summary>
    public SensorSnapshotDto GetSnapshot()
    {
        lock (_gate)
        {
            return _latest;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _computer.Open();
            _logger.LogInformation("LibreHardwareMonitor opened: {Computer}", _computer.GetReport());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open LibreHardwareMonitor (needs admin/SYSTEM context): {Message}", ex.Message);
            lock (_gate)
            {
                _latest = new SensorSnapshotDto
                {
                    IsConnected = false,
                    LastUpdate = DateTime.UtcNow,
                    Readings = []
                };
            }
            return;
        }

        try
        {
            var updateVisitor = new UpdateVisitor();
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _computer.Accept(updateVisitor);
                    lock (_gate)
                    {
                        _latest = BuildSnapshot(_computer.Hardware);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sensor poll error: {Message}", ex.Message);
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("LhmSensorReader: sensor poll loop cancelled (normal shutdown).");
            // normal shutdown
        }
        finally
        {
            try
            {
                _computer.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing LibreHardwareMonitor: {Message}", ex.Message);
            }
        }
    }

    private static SensorSnapshotDto BuildSnapshot(IEnumerable<IHardware> hardware)
    {
        var readings = new List<SensorReadingDto>();
        var now = DateTime.UtcNow;

        foreach (var hardwareItem in hardware)
        {
            Collect(hardwareItem, readings);
        }

        return new SensorSnapshotDto
        {
            IsConnected = true,
            LastUpdate = now,
            Readings = readings
        };
    }

    private static void Collect(IHardware hardware, List<SensorReadingDto> sink)
    {
        foreach (var sensor in hardware.Sensors)
        {
            float? value = sensor.Value;
            if (!value.HasValue)
                continue;

            var history = sensor.Values.ToList();
            float avg = history.Count > 0 ? history.Average(v => v.Value) : value.Value;

            sink.Add(new SensorReadingDto
            {
                SensorId = sensor.Identifier.ToString(),
                SensorName = sensor.Name,
                HardwareName = hardware.Name,
                HardwareType = hardware.HardwareType.ToString(),
                SensorType = sensor.SensorType.ToString(),
                Unit = UnitFor(sensor.SensorType),
                Value = value.Value,
                Min = sensor.Min ?? value.Value,
                Max = sensor.Max ?? value.Value,
                Avg = avg
            });
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            Collect(subHardware, sink);
        }
    }

    private static string UnitFor(SensorType type) => type switch
    {
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Frequency => "Hz",
        SensorType.Fan => "RPM",
        SensorType.Flow => "L/h",
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Factor => "",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "MB/s",
        SensorType.TimeSpan => "s",
        SensorType.Timing => "ns",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        SensorType.Conductivity => "µS/cm",
        SensorType.Humidity => "%",
        _ => ""
    };

    /// <summary>
    /// LibreHardwareMonitor ships an internal UpdateVisitor that is not public,
    /// so provide an equivalent visitor that refreshes every sensor reading.
    /// </summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
                subHardware.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}
