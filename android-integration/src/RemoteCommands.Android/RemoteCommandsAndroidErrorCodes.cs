namespace RemoteCommands.Android;

/// <summary>
/// Standard MyPowerTools runtime error codes, duplicated as literals so the Android module payload
/// does not drag the gRPC/protobuf contract package onto the phone. A test asserts these values still
/// equal the protocol constants.
/// </summary>
internal static class RemoteCommandsAndroidErrorCodes
{
    public const string NotFound = "MPT_NOT_FOUND";
    public const string RuntimeUnavailable = "MPT_RUNTIME_UNAVAILABLE";
    public const string ValidationFailed = "MPT_VALIDATION_FAILED";
    public const string PermissionRequired = "MPT_PERMISSION_REQUIRED";
    public const string CapabilityMissing = "MPT_CAPABILITY_MISSING";
}
