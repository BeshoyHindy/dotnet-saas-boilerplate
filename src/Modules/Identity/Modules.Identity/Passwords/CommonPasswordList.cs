using System.Collections.Frozen;
using System.Text;
using Boilerplate.Modules.Identity.Contracts.Services;

namespace Boilerplate.Modules.Identity.Passwords;

/// <summary>
/// The common-password list (ASVS 5.0 V6.2.4), read once from the embedded
/// <c>Passwords/common-passwords.txt</c> and held as a case-insensitive frozen set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source and licence of <c>common-passwords.txt</c>.</b> It is
/// <c>Passwords/Common-Credentials/Pwdb_top-100000.txt</c> from SecLists
/// (https://github.com/danielmiessler/SecLists, commit <c>49c9fcd20e0945f24ec854872f265eb3d13c3741</c>,
/// MIT licence), which republishes the top 100,000 of Pwdb-Public
/// (https://github.com/ignis-sec/Pwdb-Public, MIT licence) — passwords ranked by frequency across
/// roughly a billion leaked credentials. Derivation: keep entries of at least
/// <see cref="IdentityModuleConstants.PasswordLength"/> (10) characters (counted as characters, not
/// UTF-8 bytes), drop the five that hold U+FFFD (mis-encoded upstream), lower-case the rest and drop
/// duplicates keeping the first (most frequent) — 9,083 entries, in frequency order, UTF-8.
/// </para>
/// <para>
/// Why the top 100,000 and not the top 10,000: V6.2.4 asks for "at least the top 3000 passwords which
/// match the application's password policy". The top 10,000 of the same list holds only 790
/// entries of ten characters or more; the top 100,000 holds over 9,000.
/// </para>
/// <para>
/// <b>If a product lowers <see cref="IdentityModuleConstants.PasswordLength"/></b>, regenerate the
/// file at the new floor: the entries between the new and the old length are not in it. A product
/// that raises it needs no change.
/// </para>
/// </remarks>
internal sealed class CommonPasswordList : ICommonPasswordList
{
    /// <summary>Manifest name of the embedded list, pinned by <c>LogicalName</c> in the csproj.</summary>
    internal const string ResourceName = "Boilerplate.Modules.Identity.Passwords.common-passwords.txt";

    private static readonly Lazy<FrozenSet<string>> List = new(Load);

    /// <summary>Every entry on the list. Exposed for tests.</summary>
    internal static FrozenSet<string> Entries => List.Value;

    public bool Contains(string? password) =>
        !string.IsNullOrEmpty(password) && List.Value.Contains(password);

    private static FrozenSet<string> Load()
    {
        using var stream = typeof(CommonPasswordList).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded common-password list '{ResourceName}' is missing from the Identity assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var entries = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                entries.Add(line);
            }
        }

        return entries.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
