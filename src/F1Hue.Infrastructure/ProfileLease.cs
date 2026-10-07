namespace F1Hue.Infrastructure;

/// <summary>OS-owned lock, released even after a crash. Never unlink the file:
/// doing so would let another process lock a different inode for the same profile.</summary>
internal sealed class ProfileLease : IDisposable
{
    private readonly FileStream _file;
    public ProfileLease(string directory)
    {
        var path = Path.Combine(directory, "service.lock");
        try { _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e)
        {
            throw new InvalidOperationException("Ce profil F1 Hue est déjà utilisé. Arrête l’autre instance avant de lancer le programme, ou utilise --data avec un autre dossier.", e);
        }
        PrivateFiles.Protect(path);
    }
    public void Dispose() => _file.Dispose();
}
