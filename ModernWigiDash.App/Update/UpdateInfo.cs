namespace ModernWigiDash.App.Update;

/// <summary>A pending update: the new version, the slim zip URL, and its SHA-256 digest.</summary>
public sealed record UpdateInfo(string Version, string ZipUrl, string Sha256);
