namespace Yalla.Infrastructure.Media;

/// <summary>
/// Turns <c>PhotoStorage:RootPath</c> into the absolute folder the photos live in.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>~</c> is this user's Yalla data folder</b>: <c>%LOCALAPPDATA%\Yalla</c> on Windows,
/// <c>~/.local/share/Yalla</c> on Linux. So <c>~/photos</c>, which Development uses, is
/// <c>%LOCALAPPDATA%\Yalla\photos</c>, and a blank value means the same.
/// </para>
/// <para>
/// It exists for worktrees. Every checkout of this repository on one machine points at the same
/// <c>Yalla</c> database, and a photo row in that database is only half of a photo - the files are
/// the other half. A root inside the checkout gave each worktree its own files, so a cover uploaded
/// from one answered 404 in the other. One folder outside every checkout gives them one set.
/// </para>
/// <para>
/// Anything else is taken as it is: an absolute path, or one relative to the working directory.
/// </para>
/// </remarks>
internal static class PhotoStorageRoot
{
    /// <summary>What a blank <c>RootPath</c> means.</summary>
    public const string DefaultRootPath = "~/photos";

    /// <summary>The folder under the per-user data folder that <c>~</c> stands for.</summary>
    public const string ApplicationFolder = "Yalla";

    /// <summary>Resolves a configured root to an absolute path.</summary>
    /// <param name="configured">The setting as read.</param>
    /// <param name="userDataFolder">
    /// The per-user data folder; defaults to <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
    /// A parameter so a test can say where it is.
    /// </param>
    /// <exception cref="InvalidOperationException">The value starts with <c>~</c> and this user has no data folder.</exception>
    public static string Resolve(string? configured, string? userDataFolder = null)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? DefaultRootPath : configured.Trim();

        if (value != "~" && !value.StartsWith("~/", StringComparison.Ordinal) && !value.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.GetFullPath(value);
        }

        var baseFolder = userDataFolder
                         ?? Environment.GetFolderPath(
                             Environment.SpecialFolder.LocalApplicationData,
                             Environment.SpecialFolderOption.DoNotVerify);

        if (string.IsNullOrWhiteSpace(baseFolder))
        {
            throw new InvalidOperationException(
                $"PhotoStorage:RootPath is '{value}', and '~' stands for this user's local application data "
                + "folder, which this machine does not have (on Linux, HOME is probably unset). Set "
                + "PhotoStorage__RootPath to an absolute, writable folder.");
        }

        var rest = value.Length > 2
            ? value[2..].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)
            : string.Empty;

        return Path.GetFullPath(Path.Combine(baseFolder, ApplicationFolder, rest));
    }
}
