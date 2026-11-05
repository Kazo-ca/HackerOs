using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Kazo.Os.Tools.WebRouteGenerator;

/// <summary>A <c>.razor</c> or <c>.cshtml</c> file of the project, as the Razor SDK hands it to analyzers.</summary>
/// <param name="TargetPath">Project-relative path with <c>/</c> separators (<c>Views/Home/Index.razor</c>).</param>
/// <param name="RootNamespace">The project's root namespace.</param>
/// <param name="NamespaceDirective">The <c>@namespace</c> the file declares, or <see langword="null"/>.</param>
internal sealed record RazorFileModel(string TargetPath, string RootNamespace, string? NamespaceDirective);

/// <summary>A view of the table: its path under <c>Views/</c> and the component class the Razor compiler generates.</summary>
/// <param name="Path">Path without folder nor extension (<c>Home/Index</c>).</param>
/// <param name="TypeName">Fully qualified class name without <c>global::</c>.</param>
internal sealed record ViewModel(string Path, string TypeName);

/// <summary>
/// Finds the views of a site in its <c>.razor</c> files (ADR 0083 §2) and names their classes the way the
/// Razor compiler does: namespace <c>&lt;RootNamespace&gt;.&lt;folders&gt;</c> (or the nearest <c>@namespace</c>,
/// in the file or an <c>_Imports.razor</c> above it), class <c>&lt;file name&gt;</c>, both sanitized into
/// C# identifiers.
/// </summary>
internal static class ViewAnalyzer
{
    private const string ViewsFolder = "Views/";
    private const string Imports = "_Imports.razor";
    private const string ViewImports = "_ViewImports.cshtml";

    /// <summary>The namespace of a <c>.cshtml</c> class without <c>@namespace</c> (Razor SDK 6.0 and later).</summary>
    internal const string CshtmlDefaultNamespace = "AspNetCoreGeneratedDocument";

    /// <summary>Describes one Razor file from its path and the analyzer options of the build.</summary>
    public static RazorFileModel Describe(AdditionalText text, AnalyzerConfigOptionsProvider options, CancellationToken cancellationToken)
    {
        AnalyzerConfigOptions fileOptions = options.GetOptions(text);
        fileOptions.TryGetValue("build_metadata.AdditionalFiles.TargetPath", out string? targetPath);
        options.GlobalOptions.TryGetValue("build_property.RootNamespace", out string? rootNamespace);
        options.GlobalOptions.TryGetValue("build_property.MSBuildProjectDirectory", out string? projectDirectory);
        if (string.IsNullOrEmpty(targetPath))
        {
            // Outside the Razor SDK (tests): relative to the project directory.
            targetPath = !string.IsNullOrEmpty(projectDirectory) && text.Path.StartsWith(projectDirectory!, StringComparison.OrdinalIgnoreCase)
                ? text.Path.Substring(projectDirectory!.Length).TrimStart('/', '\\')
                : Path.GetFileName(text.Path);
        }
        else
        {
            targetPath = DecodeTargetPath(targetPath!);
        }

        targetPath = targetPath.Replace('\\', '/');
        string? directive = null;
        if (text.GetText(cancellationToken) is SourceText source)
        {
            foreach (TextLine line in source.Lines)
            {
                string content = line.ToString().Trim();
                if (content.StartsWith("@namespace ", StringComparison.Ordinal))
                {
                    directive = content.Substring("@namespace ".Length).Trim();
                    break;
                }
            }
        }

        return new RazorFileModel(targetPath, string.IsNullOrEmpty(rootNamespace) ? "Views" : rootNamespace!, directive);
    }

    /// <summary>
    /// Turns the Razor files into the view table: every <c>.razor</c> and <c>.cshtml</c> file under
    /// <c>Views/</c> except the imports (<c>_Imports.razor</c>, <c>_ViewImports.cshtml</c>).
    /// </summary>
    public static ImmutableArray<ViewModel> CollectViews(ImmutableArray<RazorFileModel> files)
    {
        // @namespace of an _Imports.razor (or _ViewImports.cshtml) applies to its folder and below, as in the Razor compiler.
        Dictionary<string, string> importNamespaces = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> viewImportNamespaces = new(StringComparer.OrdinalIgnoreCase);
        foreach (RazorFileModel file in files)
        {
            string name = Path.GetFileName(file.TargetPath);
            if (file.NamespaceDirective is not null && name.Equals(Imports, StringComparison.OrdinalIgnoreCase))
            {
                importNamespaces[DirectoryOf(file.TargetPath)] = file.NamespaceDirective;
            }
            else if (file.NamespaceDirective is not null && name.Equals(ViewImports, StringComparison.OrdinalIgnoreCase))
            {
                viewImportNamespaces[DirectoryOf(file.TargetPath)] = file.NamespaceDirective;
            }
        }

        List<ViewModel> views = [];
        foreach (RazorFileModel file in files.OrderBy(file => file.TargetPath, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file.TargetPath);
            if (!file.TargetPath.StartsWith(ViewsFolder, StringComparison.OrdinalIgnoreCase)
                || name.Equals(Imports, StringComparison.OrdinalIgnoreCase)
                || name.Equals(ViewImports, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isCshtml = file.TargetPath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
            string withoutExtension = file.TargetPath.Substring(0, file.TargetPath.Length - (isCshtml ? ".cshtml" : ".razor").Length);
            string viewPath = withoutExtension.Substring(ViewsFolder.Length);
            if (isCshtml)
            {
                views.Add(new ViewModel(viewPath, CshtmlTypeName(file, withoutExtension, viewImportNamespaces)));
                continue;
            }

            string className = SanitizeIdentifier(Path.GetFileName(withoutExtension));
            string ns = file.NamespaceDirective ?? NearestImportNamespace(importNamespaces, DirectoryOf(file.TargetPath)) ?? DefaultNamespace(file);
            views.Add(new ViewModel(viewPath, ns.Length == 0 ? className : $"{ns}.{className}"));
        }

        return [.. views];
    }

    /// <summary>
    /// Names a <c>.cshtml</c> class as the Razor compiler's MVC mode does (ADR 0086 §4): the class is the
    /// whole path without extension, sanitized (<c>Views/Home/Index</c> becomes <c>Views_Home_Index</c>); the
    /// namespace is the file's own <c>@namespace</c>, or the nearest <c>_ViewImports.cshtml</c> one followed
    /// by the folders between it and the file, or <c>AspNetCoreGeneratedDocument</c>.
    /// </summary>
    private static string CshtmlTypeName(RazorFileModel file, string withoutExtension, Dictionary<string, string> viewImportNamespaces)
    {
        string className = SanitizeIdentifier(withoutExtension);
        string? ns = file.NamespaceDirective;
        if (ns is null)
        {
            string directory = DirectoryOf(file.TargetPath);
            for (string current = directory; ; current = DirectoryOf(current))
            {
                if (viewImportNamespaces.TryGetValue(current, out string? imported))
                {
                    string relative = directory.Length > current.Length ? directory.Substring(current.Length).TrimStart('/') : string.Empty;
                    ns = string.Join(".", new[] { imported }.Concat(relative.Split(['/'], StringSplitOptions.RemoveEmptyEntries).Select(SanitizeIdentifier)));
                    break;
                }

                if (current.Length == 0)
                {
                    break;
                }
            }
        }

        return $"{ns ?? CshtmlDefaultNamespace}.{className}";
    }

    private static string DefaultNamespace(RazorFileModel file)
    {
        // The Razor compiler sanitizes the root namespace segment by segment too (My.Root-Ns → My.Root_Ns).
        StringBuilder ns = new(string.Join(".", file.RootNamespace.Split('.').Select(SanitizeIdentifier)));
        foreach (string segment in DirectoryOf(file.TargetPath).Split(['/'], StringSplitOptions.RemoveEmptyEntries))
        {
            ns.Append('.').Append(SanitizeIdentifier(segment));
        }

        return ns.ToString();
    }

    private static string? NearestImportNamespace(Dictionary<string, string> importNamespaces, string directory)
    {
        for (string current = directory; ; current = DirectoryOf(current))
        {
            if (importNamespaces.TryGetValue(current, out string? ns))
            {
                return ns;
            }

            if (current.Length == 0)
            {
                return null;
            }
        }
    }

    private static string DirectoryOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path.Substring(0, slash);
    }

    private static string DecodeTargetPath(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return value;
        }
    }

    /// <summary>Razor's <c>CSharpIdentifier.SanitizeIdentifier</c>: invalid characters become <c>_</c>, a leading digit gets one before it.</summary>
    internal static string SanitizeIdentifier(string name)
    {
        if (name.Length == 0)
        {
            return "_";
        }

        StringBuilder builder = new(name.Length + 1);
        if (!SyntaxFacts.IsIdentifierStartCharacter(name[0]) && SyntaxFacts.IsIdentifierPartCharacter(name[0]))
        {
            builder.Append('_');
        }

        foreach (char character in name)
        {
            builder.Append(SyntaxFacts.IsIdentifierPartCharacter(character) ? character : '_');
        }

        return builder.ToString();
    }
}
