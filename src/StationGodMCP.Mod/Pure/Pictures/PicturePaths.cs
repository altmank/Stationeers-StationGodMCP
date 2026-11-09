#nullable enable

using System;
using System.IO;

namespace StationGodMCP.Pure.Pictures;

/// <summary>
/// Where show picture may write (catalogue show.json, picture; [Pictures] Folder): only inside the configured root.
/// file is a plain name ending .png (letters, digits, space, - _ . and nothing else; not starting with a dot or a
/// space); folder, when given, is plain names of the same characters joined by / or \, none of them . or ..; and the
/// full path, after Path.GetFullPath, must still lie under the root. Windows' reserved device names (CON, PRN, AUX, NUL,
/// COM1-9, LPT1-9, with any extension) are refused, and a path too long for the system is refused rather than thrown.
/// Anything else is refused bad_path, so a caller (a remote client over TCP included) cannot name a file anywhere else
/// on the machine. FirstReparsePoint then finds a junction or symbolic link between the root and the file (the game
/// asks the file system). Pure: no file is touched.
/// </summary>
internal static class PicturePaths
{
    internal const string BadPath = "bad_path";
    internal const int MaximumNameLength = 100;
    internal const int MaximumFolderDepth = 4;

    /// <summary>The full path of the picture, or the reason it is refused (bad_path) in problem.</summary>
    internal static string? Resolve(string root, string? folder, string? file, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
        {
            problem = "the pictures folder ([Pictures] Folder) is not an absolute path.";
            return null;
        }

        if (file == null || !IsPlainName(file) || !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            file.Length <= ".png".Length)
        {
            problem = $"file '{file}' is not a plain .png name (letters, digits, space, - _ . only, up to " +
                      $"{MaximumNameLength} characters, no folder in it).";
            return null;
        }

        string fullRoot = Path.GetFullPath(root);
        string directory = fullRoot;
        if (!string.IsNullOrEmpty(folder))
        {
            string[] parts = folder!.Split('/', '\\');
            if (parts.Length > MaximumFolderDepth)
            {
                problem = $"folder '{folder}' is deeper than {MaximumFolderDepth} names.";
                return null;
            }

            foreach (string part in parts)
            {
                if (!IsPlainName(part))
                {
                    problem = $"folder '{folder}' is not plain names under the pictures folder (letters, digits, space, " +
                              "- _ . only; no .., no drive, no leading /).";
                    return null;
                }

                directory = Path.Combine(directory, part);
            }
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(directory, file));
        }
        catch (Exception error) when (error is PathTooLongException || error is ArgumentException ||
                                      error is NotSupportedException)
        {
            problem = $"the path under the pictures folder is not usable ({error.GetType().Name}: {error.Message}).";
            return null;
        }

        if (!IsUnder(fullRoot, full))
        {
            problem = $"'{full}' is outside the pictures folder '{fullRoot}'.";
            return null;
        }

        return full;
    }

    /// <summary>
    /// The first folder below root on the way to full, or full itself, that isReparsePoint says is a junction or a
    /// symbolic link (one that exists); null when none is. root itself is the owner's choice and is not checked.
    /// </summary>
    internal static string? FirstReparsePoint(string root, string full, Func<string, bool> isReparsePoint)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rest = full.Substring(fullRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string current = fullRoot;
        foreach (string part in rest.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0)
            {
                continue;
            }

            current = Path.Combine(current, part);
            if (isReparsePoint(current))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>Whether full lies inside root (a separator after root's own text; case-insensitive, as Windows is).</summary>
    internal static bool IsUnder(string root, string full)
    {
        string withSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                               root.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;
        return full.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase) && full.Length > withSeparator.Length;
    }

    /// <summary>Windows' device names, which open a device rather than a file: CON, PRN, AUX, NUL, COM1-9, LPT1-9, with any extension.</summary>
    internal static bool IsReserved(string name)
    {
        int dot = name.IndexOf('.');
        string stem = (dot >= 0 ? name.Substring(0, dot) : name).TrimEnd(' ').ToUpperInvariant();
        if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL")
        {
            return true;
        }

        return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                    stem.StartsWith("LPT", StringComparison.Ordinal)) &&
               stem[3] >= '1' && stem[3] <= '9';
    }

    /// <summary>
    /// A name of letters, digits, space, - _ and ., not empty, not . or .., not starting with a dot or space nor ending
    /// with a dot or space (Windows drops those), not a reserved device name, at most MaximumNameLength characters.
    /// </summary>
    internal static bool IsPlainName(string name)
    {
        if (name.Length == 0 || name.Length > MaximumNameLength || name[0] == '.' || name[0] == ' ' ||
            name[name.Length - 1] == '.' || name[name.Length - 1] == ' ')
        {
            return false;
        }

        if (IsReserved(name))
        {
            return false;
        }

        foreach (char c in name)
        {
            bool plain = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ' ||
                         c == '-' || c == '_' || c == '.';
            if (!plain)
            {
                return false;
            }
        }

        return true;
    }
}
