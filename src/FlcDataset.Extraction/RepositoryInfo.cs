using System.Diagnostics;
using FlcDataset.Core;

namespace FlcDataset.Extraction;

public sealed record LicenseInfo(string? Spdx, string Reason, string? LicenseFile, bool Allowed);

public sealed record RepositoryInfo
{
    public required string Root { get; init; }
    public required string RepositoryId { get; init; }
    public string? Revision { get; init; }
    public string? RevisionError { get; init; }
    public bool? Dirty { get; init; }
    public string? OriginUrl { get; init; }
    public string? CommitDate { get; init; }
    public required LicenseInfo License { get; init; }
    public string? GlobalJsonSdk { get; init; }

    public static RepositoryInfo Inspect(string root, DatasetConfig config)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository not found: {root}");
        var (top, _) = Git(root, "rev-parse", "--show-toplevel");
        // Only a checkout root identifies a revision; a subdirectory of some other repository must not inherit its SHA.
        bool isRoot = top is not null && Path.GetFullPath(top).TrimEnd('/') == root.TrimEnd('/');
        var (rev, revErr) = isRoot ? Git(root, "rev-parse", "HEAD") : (null, "not_a_git_checkout_root");
        var (status, _) = Git(root, "status", "--porcelain", "--untracked-files=no");
        var (origin, _) = Git(root, "config", "--get", "remote.origin.url");
        var (date, _) = Git(root, "log", "-1", "--format=%cI");
        return new RepositoryInfo
        {
            Root = root,
            RepositoryId = config.RepositoryId,
            Revision = rev is { Length: 40 } ? rev : null,
            RevisionError = rev is { Length: 40 } ? null : revErr ?? "not_a_git_repository",
            Dirty = rev is null || status is null ? null : status.Length > 0,
            OriginUrl = origin,
            CommitDate = date,
            License = DetectLicense(root, config.License),
            GlobalJsonSdk = ReadGlobalJsonSdk(root),
        };
    }

    static string? ReadGlobalJsonSdk(string root)
    {
        var p = Path.Combine(root, "global.json");
        if (!File.Exists(p)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p),
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return "unparseable"; }
    }

    /// <summary>Conservative license detection from the root LICENSE file text. Unknown is never upgraded to allowed silently.</summary>
    public static LicenseInfo DetectLicense(string root, LicenseConfig cfg)
    {
        string[] names = ["LICENSE", "LICENSE.md", "LICENSE.txt", "LICENCE", "COPYING", "license.md", "License.txt"];
        var file = names.Select(n => Path.Combine(root, n)).FirstOrDefault(File.Exists);
        string? detected = null;
        string reason;
        if (file is null) reason = "no_license_file";
        else
        {
            var text = File.ReadAllText(file);
            if (text.Contains("Permission is hereby granted, free of charge", StringComparison.Ordinal) &&
                text.Contains("THE SOFTWARE IS PROVIDED \"AS IS\"", StringComparison.OrdinalIgnoreCase))
                detected = "MIT";
            else if (text.Contains("Apache License", StringComparison.Ordinal) && text.Contains("Version 2.0", StringComparison.Ordinal))
                detected = "Apache-2.0";
            else if (text.Contains("Redistribution and use in source and binary forms", StringComparison.Ordinal)
                     && text.Contains("THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS \"AS IS\"", StringComparison.OrdinalIgnoreCase))
                detected = text.Contains("Neither the name", StringComparison.OrdinalIgnoreCase) ? "BSD-3-Clause" : "BSD-2-Clause";
            reason = detected is null ? "license_text_unrecognized" : $"license_file_text_match:{Path.GetFileName(file)}";
        }
        if (cfg.Declared is not null && detected is not null && cfg.Declared != detected)
            return new LicenseInfo(null, $"declared_{cfg.Declared}_conflicts_with_detected_{detected}", file, false);
        var spdx = detected;
        return new LicenseInfo(spdx, reason, file is null ? null : Path.GetFileName(file), spdx is not null && cfg.Allowlist.Contains(spdx));
    }

    public static (string? Output, string? Error) Git(string root, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(root);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? (stdout.Trim(), null) : (null, stderr.Trim());
        }
        catch (System.ComponentModel.Win32Exception e) { return (null, "git_unavailable: " + e.Message); }
    }
}
