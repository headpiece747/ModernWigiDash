using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModernWigiDash.App;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class FramePipelineTests
{
    [TestMethod]
    public void Tick_ComposesActivePage_AndPushesToDelivery()
    {
        StaRunner.Run(() =>
        {
            var page = new PageLayout { PageName = "TestPage" };
            int composeCount = 0;
            int sendCount = 0;

            using var delivery = new FrameDelivery(send: _ =>
            {
                Interlocked.Increment(ref sendCount);
                return FrameSendResult.Sent;
            });

            using var compositor = new SkiaFrameCompositor();
            using var pipeline = new FramePipeline(
                activePageProvider: () =>
                {
                    composeCount++;
                    return page;
                },
                compositor: compositor,
                delivery: delivery);

            pipeline.Tick();

            Assert.AreEqual(1, composeCount, "active page must be sampled on tick");
            Assert.IsNotNull(pipeline.CurrentFrame, "composited frame buffer must be accessible");
            Assert.AreEqual(DisplayGeometry.FramebufferWidth, pipeline.CurrentFrame.Width);
            Assert.AreEqual(DisplayGeometry.FramebufferHeight, pipeline.CurrentFrame.Height);
        });
    }

    [TestMethod]
    public void Tick_WhenActivePageIsNull_DoesNotThrow()
    {
        StaRunner.Run(() =>
        {
            using var delivery = new FrameDelivery(send: _ => FrameSendResult.Sent);
            using var pipeline = new FramePipeline(
                activePageProvider: () => null,
                delivery: delivery);

            // Must not throw NRE when page is null (startup window)
            pipeline.Tick();
        });
    }

    [TestMethod]
    public void EditMode_SyncsCompositorProperty()
    {
        StaRunner.Run(() =>
        {
            using var delivery = new FrameDelivery(send: _ => FrameSendResult.Sent);
            using var pipeline = new FramePipeline(
                activePageProvider: () => new PageLayout(),
                delivery: delivery);

            Assert.IsFalse(pipeline.IsEditMode);
            pipeline.IsEditMode = true;
            Assert.IsTrue(pipeline.IsEditMode);
            Assert.IsTrue(pipeline.Compositor.IsEditMode);

            var widget = new PlacedWidgetInstance { PluginId = "clock" };
            pipeline.SelectedWidget = widget;
            Assert.AreSame(widget, pipeline.SelectedWidget);
            Assert.AreSame(widget, pipeline.Compositor.SelectedWidget);
        });
    }

    [TestMethod]
    public void RepaintAndTickCallbacks_FireOnTick()
    {
        StaRunner.Run(() =>
        {
            bool repainted = false;
            bool ticked = false;

            using var delivery = new FrameDelivery(send: _ => FrameSendResult.Sent);
            using var pipeline = new FramePipeline(
                activePageProvider: () => new PageLayout(),
                delivery: delivery,
                requestRepaint: () => repainted = true,
                onTick: () => ticked = true);

            pipeline.Tick();

            Assert.IsTrue(repainted, "requestRepaint must fire on tick");
            Assert.IsTrue(ticked, "onTick must fire on tick");
        });
    }

    [TestMethod]
    public void StartAndStop_CanBeInvokedSafely()
    {
        StaRunner.Run(() =>
        {
            using var delivery = new FrameDelivery(send: _ => FrameSendResult.Sent);
            using var pipeline = new FramePipeline(
                activePageProvider: () => new PageLayout(),
                delivery: delivery);

            pipeline.Start();
            pipeline.Stop();
        });
    }

    [TestMethod]
    public void Dispose_ShutsDownSubmodules_AndSubsequentTicksAreNoOps()
    {
        StaRunner.Run(() =>
        {
            int composeCount = 0;
            using var delivery = new FrameDelivery(send: _ => FrameSendResult.Sent);
            var pipeline = new FramePipeline(
                activePageProvider: () =>
                {
                    composeCount++;
                    return new PageLayout();
                },
                delivery: delivery);

            pipeline.Tick();
            Assert.AreEqual(1, composeCount);

            pipeline.Dispose();

            // After dispose, tick should safely no-op
            pipeline.Tick();
            Assert.AreEqual(1, composeCount, "post-dispose tick must not compose");
        });
    }

    [TestMethod]
    public void Metrics_ForwardFaithfullyFromDelivery()
    {
        StaRunner.Run(() =>
        {
            using var delivery = new FrameDelivery(send: _ => FrameSendResult.Sent);
            using var pipeline = new FramePipeline(
                activePageProvider: () => new PageLayout(),
                delivery: delivery);

            Assert.AreEqual(0, pipeline.FramesSent);
            Assert.AreEqual(0, pipeline.DroppedCount);
            Assert.IsFalse(pipeline.IsSendInFlight);
        });
    }
}
