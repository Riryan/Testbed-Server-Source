using System.Threading;
using System.Threading.Tasks;

namespace Game.Server.Application.Authentication
{
    /// <summary>
    /// Legacy verifier-based account boundary retained for migration/regression tests.
    /// Deployed login/account creation is owned by BackendServer's HTTPS surface; Unity
    /// gameplay sessions receive only one-time admission tokens.
    /// </summary>
    public interface IAccountAuthenticationService
    {
        Task<AccountAuthenticationResult> LoginAsync(
            string account,
            string passwordVerifier,
            CancellationToken cancellationToken);

        Task<AccountAuthenticationResult> CreateAsync(
            string account,
            string passwordVerifier,
            CancellationToken cancellationToken);
    }
}
