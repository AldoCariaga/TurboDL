using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace TurboDL.Core.Ipc;

/// <summary>Who may serve and use the pipe: this Windows user, whether or not either side runs elevated.</summary>
[SupportedOSPlatform("windows")]
internal static class IpcPipeSecurity
{
    public static SecurityIdentifier CurrentUser
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User ?? throw new InvalidOperationException("The current Windows account has no SID.");
        }
    }

    /// <summary>
    /// Refuses a pipe served by anyone but this user, so another account can't pose as TurboDL and collect
    /// browser cookies. The token's default owner is accepted too (the Administrators group when elevated).
    /// </summary>
    public static void VerifyServer(PipeStream pipe)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        using var identity = WindowsIdentity.GetCurrent();
        if (owner is not null && (owner.Equals(identity.User) || owner.Equals(identity.Owner))) return;
        throw new UnauthorizedAccessException("The TurboDL pipe is not owned by the current user.");
    }
}
