namespace Kazo.Os.Simulation.Abstractions.Network;

/// <summary>
/// Renders a <see cref="SimulatedPage"/> as plain text for terminal commands (<c>curl</c>, <c>cat</c>)
/// that print fetched page content. Kept alongside <see cref="SimulatedPage"/> itself, rather than in
/// any one command project, since both commands need the exact same rendering — extracted here instead
/// of duplicated once a second consumer needed it.
/// </summary>
public static class SimulatedPageTextFormatter
{
    /// <summary>Writes <paramref name="page"/>'s sections to <paramref name="writer"/> as plain text.</summary>
    public static void WriteTo(TextWriter writer, SimulatedPage page)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(page);

        writer.WriteLine($"<!-- Title: {page.Title} -->");
        writer.WriteLine();

        foreach (var section in page.Sections)
        {
            switch (section)
            {
                case HeroSection h:
                    writer.WriteLine($"=== {h.Headline} ===");
                    if (h.Subtitle is not null) writer.WriteLine(h.Subtitle);
                    writer.WriteLine();
                    break;

                case ParagraphSection p:
                    writer.WriteLine(p.Text);
                    writer.WriteLine();
                    break;

                case ListSection l:
                    if (l.Title is not null) writer.WriteLine(l.Title + ":");
                    foreach (var item in l.Items) writer.WriteLine("  * " + item);
                    writer.WriteLine();
                    break;

                case AlertSection a:
                    writer.WriteLine($"[{a.Level.ToString().ToUpperInvariant()}] {a.Message}");
                    writer.WriteLine();
                    break;

                case NavigationSection n:
                    writer.WriteLine(string.Join("  |  ", n.Links.Select(lk => lk.Label)));
                    writer.WriteLine();
                    break;

                case LoginFormSection lf:
                    writer.WriteLine($"[FORM: {lf.Title}]");
                    writer.WriteLine($"  {lf.UsernameLabel}: _______");
                    writer.WriteLine($"  {lf.PasswordLabel}: _______");
                    writer.WriteLine($"  [{lf.SubmitLabel}] -> POST {lf.PostPath}");
                    writer.WriteLine();
                    break;

                case FormSection fs:
                    writer.WriteLine($"[FORM: {fs.Title}]");
                    foreach (var field in fs.Fields)
                        writer.WriteLine($"  {field.Label}: _______");
                    writer.WriteLine($"  [{fs.SubmitLabel}] -> POST {fs.PostPath}");
                    writer.WriteLine();
                    break;

                case ProductGridSection g:
                    foreach (var p in g.Products)
                    {
                        writer.WriteLine($"  [{p.Title}] - {p.Price}");
                        writer.WriteLine($"    {p.Description}");
                    }
                    writer.WriteLine();
                    break;

                case TableSection t:
                    if (t.Title is not null) writer.WriteLine(t.Title);
                    writer.WriteLine(string.Join(" | ", t.Headers));
                    foreach (var row in t.Rows)
                        writer.WriteLine(string.Join(" | ", row));
                    writer.WriteLine();
                    break;

                case ForumSection f:
                    writer.WriteLine($"--- {f.SectionTitle} ---");
                    foreach (var th in f.Threads)
                        writer.WriteLine($"  {(th.IsHot ? "[HOT] " : "")}{th.Title}  [{th.Author}, {th.TimestampDisplay}, {th.Views} views]");
                    writer.WriteLine();
                    break;

                case EmailListSection e:
                    foreach (var em in e.Emails)
                        writer.WriteLine($"  {(em.IsRead ? "" : "[UNREAD] ")}{em.Subject} — {em.Sender}");
                    writer.WriteLine();
                    break;
            }
        }
    }
}
