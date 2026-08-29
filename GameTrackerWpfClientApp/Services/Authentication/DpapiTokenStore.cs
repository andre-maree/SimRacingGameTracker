using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GameTrackerWpfClientApp.Services.Authentication;

/// <summary>
/// Stores the access token encrypted with DPAPI under the current user's profile.
/// </summary>
/// <remarks>
/// A JWT is a bearer credential: anyone holding the file contents can act as the user
/// until it expires. <see cref="DataProtectionScope.CurrentUser"/> ties the ciphertext to
/// the logged-on Windows account, so another user on the same machine (and any file
/// copied off it) cannot decrypt it. The key is managed by Windows, which is why no
/// key material appears anywhere in this project.
/// </remarks>
public sealed class DpapiTokenStore : ITokenStore
{
    /// <summary>
    /// Additional entropy mixed into the encryption. It is not a secret; it scopes the
    /// ciphertext to this application so an unrelated DPAPI blob cannot be swapped in.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GameTracker.TokenStore.v1");

    private readonly string _tokenFilePath;
    private readonly ILogger<DpapiTokenStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiTokenStore(string localDataPath, ILogger<DpapiTokenStore> logger)
    {
        _tokenFilePath = Path.Combine(localDataPath, "token.dat");
        _logger = logger;
    }

    public async Task<StoredToken?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_tokenFilePath))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(_tokenFilePath, cancellationToken);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<StoredToken>(plainBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // A corrupt, tampered or foreign-profile token file is not a fatal error: the
            // worst case is that the user signs in again. Deleting it prevents the app
            // from failing the same way on every start.
            _logger.LogWarning(ex, "Stored token could not be read; discarding it and requiring a fresh sign-in.");
            TryDelete();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(StoredToken token, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var plainBytes = JsonSerializer.SerializeToUtf8Bytes(token);
            var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);

            // Write to a temporary file and move it into place, so a crash mid-write
            // cannot leave a half-written token that fails to decrypt on next launch.
            var temporaryPath = _tokenFilePath + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
            File.Move(temporaryPath, _tokenFilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            TryDelete();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TryDelete()
    {
        try
        {
            File.Delete(_tokenFilePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete the stored token file at {Path}.", _tokenFilePath);
        }
    }
}
