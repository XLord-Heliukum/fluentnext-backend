using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using FluentNext.Backend;
using Markdig;
using YamlDotNet.Serialization;
using HtmlAgilityPack;
using System.Diagnostics.CodeAnalysis;

// ───────────────────────────────────────────────────────────────────────────
// FluentNext 后端生成器
//   读 Markdown(front-matter + 正文) → 产出 content.json / atom.xml /
//   sitemap.xml / search.xml，取代 Hexo。前端 Blazor 一行不改。
//
// 用法:
//   FluentNext.Backend --input <posts目录> --output <输出目录> \
//                       [--site site.json] [--images <图片目录>]
// ───────────────────────────────────────────────────────────────────────────

var (inputDir, outputDir, sitePath, imagesDir) = ParseArgs(args);

if (!Directory.Exists(inputDir))
    Fail($"文章目录不存在: {inputDir}");
if (!File.Exists(sitePath))
    Fail($"站点配置不存在: {sitePath}");

var site = JsonSerializer.Deserialize<SiteConfig>(File.ReadAllText(sitePath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
if (site is null) Fail($"无法解析 {sitePath}");

if (string.IsNullOrWhiteSpace(site.Url))
    Fail("site.json 缺少 url");

var pipeline = new MarkdownPipelineBuilder()
    .UseFootnotes()
    .UseEmphasisExtras()   // ==高亮(mark) / ~下标 / ^上标 / ~~删除线 / ++插入
    .UseTaskLists()
    .UsePipeTables()
    .UseGridTables()
    .UseAutoLinks()
    .Build();

var posts = new List<BlogPost>();
foreach (var md in Directory.GetFiles(inputDir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
{
    var raw = File.ReadAllText(md);
    SplitFrontMatter(raw, out var fmText, out var body);

    var fm = new DeserializerBuilder().Build()
        .Deserialize<Dictionary<string, object>>(new StringReader(fmText))
        ?? new Dictionary<string, object>();

    string Str(string k) => fm.TryGetValue(k, out var v) ? (v?.ToString() ?? "") : "";
    List<string> List(string k)
    {
        if (fm.TryGetValue(k, out var v))
        {
            if (v is System.Collections.IEnumerable en and not string)
                return en.Cast<object>().Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
            if (v is string s)
                return s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return new List<string>();
    }

    var slug = Path.GetFileNameWithoutExtension(md);
    var title = Str("title");
    if (string.IsNullOrWhiteSpace(title)) title = slug;

    var html = Markdown.ToHtml(body, pipeline);
    var (htmlWithIds, toc) = InjectHeadingIds(html);

    var permalink = site.Url.TrimEnd('/') + "/post/" + slug + "/";

    posts.Add(new BlogPost
    {
        Title = title,
        Slug = slug,
        Permalink = permalink,
        Date = ParseDate(Str("date"), md),
        Updated = !string.IsNullOrWhiteSpace(Str("updated")) ? ParseDate(Str("updated"), md) : FileUtc(md),
        Excerpt = MakeExcerpt(htmlWithIds, 150),
        Content = htmlWithIds,
        Toc = toc,
        Categories = List("categories"),
        Tags = List("tags"),
    });

    Console.WriteLine($"  ✓ {slug}  ({toc.Count} 条目录 / {posts[^1].Content.Length} 字节 HTML)");
}

posts = posts.OrderByDescending(p => p.Date, StringComparer.Ordinal).ToList();

// 聚合 分类 / 标签 / 归档
var categories = posts.SelectMany(p => p.Categories)
    .GroupBy(c => c, StringComparer.Ordinal)
    .Select(g => new CategoryInfo { Name = g.Key, Slug = g.Key, Count = g.Count() })
    .OrderByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();

var tags = posts.SelectMany(p => p.Tags)
    .GroupBy(t => t, StringComparer.Ordinal)
    .Select(g => new TagInfo { Name = g.Key, Slug = g.Key, Count = g.Count() })
    .OrderByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();

var archives = posts.GroupBy(p => int.Parse(p.Date[..4], CultureInfo.InvariantCulture))
    .Select(g => new ArchiveInfo
    {
        Year = g.Key,
        Count = g.Count(),
        Months = g.GroupBy(p => int.Parse(p.Date.Substring(5, 2), CultureInfo.InvariantCulture))
                  .Select(m => new ArchiveMonth { Month = m.Key, Count = m.Count() })
                  .OrderBy(m => m.Month).ToList(),
    })
    .OrderByDescending(a => a.Year).ToList();

var data = new ContentData
{
    Site = new SiteInfo
    {
        Title = site.Title,
        Description = site.Description,
        Url = site.Url,
        Menu = site.Menu,
    },
    Posts = posts,
    Categories = categories,
    Tags = tags,
    Archives = archives,
};

// 写出
Directory.CreateDirectory(outputDir);
Directory.CreateDirectory(Path.Combine(outputDir, "api"));

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文原样输出
};
File.WriteAllText(Path.Combine(outputDir, "api", "content.json"),
    JsonSerializer.Serialize(data, jsonOpts));

WriteAtomXml(Path.Combine(outputDir, "atom.xml"), site, posts);
WriteSitemapXml(Path.Combine(outputDir, "sitemap.xml"), site, posts);
WriteSearchXml(Path.Combine(outputDir, "search.xml"), posts);

if (!string.IsNullOrWhiteSpace(imagesDir) && Directory.Exists(imagesDir))
{
    CopyDir(imagesDir, Path.Combine(outputDir, "images"));
    Console.WriteLine($"  ✓ 已拷贝图片: {imagesDir} → {Path.Combine(outputDir, "images")}");
}

Console.WriteLine($"\n完成: {posts.Count} 篇文章 → {Path.Combine(outputDir, "api", "content.json")}");

// ── 辅助方法 ──────────────────────────────────────────────────────────────

static (string html, List<TocItem> toc) InjectHeadingIds(string html)
{
    var doc = new HtmlDocument();
    doc.LoadHtml(html);
    var toc = new List<TocItem>();
    var seen = new Dictionary<string, int>(StringComparer.Ordinal);

    foreach (var node in doc.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4|//h5|//h6")
             ?? Enumerable.Empty<HtmlNode>())
    {
        var level = int.Parse(node.Name[1..], CultureInfo.InvariantCulture);
        var text = node.InnerText.Trim();
        var baseSlug = Slugify(text);
        var id = baseSlug;
        if (seen.TryGetValue(baseSlug, out var n))
        {
            seen[baseSlug] = n + 1;
            id = baseSlug + "-" + (n + 1);
        }
        else
        {
            seen[baseSlug] = 1;
        }
        node.SetAttributeValue("id", id);
        if (level is >= 2 and <= 4)
            toc.Add(new TocItem { Level = level, Text = text, Id = id });
    }
    return (doc.DocumentNode.OuterHtml, toc);
}

/// <summary>复刻 markdown-it-anchor 默认 slugify（与线上 content.json 锚点一致）。</summary>
static string Slugify(string s)
{
    var sb = new StringBuilder(s.Length);
    foreach (var c in s.ToLowerInvariant())
    {
        if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            sb.Append('-');
        else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-'
                 || (c >= 0x4e00 && c <= 0x9fff))
            sb.Append(c);
        // 其余字符（标点、空格外的特殊符号）直接丢弃
    }
    return sb.ToString();
}

static string MakeExcerpt(string html, int max)
{
    var doc = new HtmlDocument();
    doc.LoadHtml(html);
    var text = doc.DocumentNode.InnerText;
    text = Regex.Replace(text, @"\s+", " ").Trim();
    return text.Length > max ? text[..max] : text;
}

static string ParseDate(string s, string file)
{
    if (!string.IsNullOrWhiteSpace(s)
        && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        return FormatUtc(dt);
    return FileUtc(file);
}

static string FileUtc(string file) => FormatUtc(File.GetLastWriteTimeUtc(file));

static string FormatUtc(DateTime dt)
{
    var utc = dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => new DateTimeOffset(dt, TimeSpan.FromHours(8)).UtcDateTime, // front-matter 视为 UTC+8
    };
    return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}

static void SplitFrontMatter(string raw, out string fm, out string body)
{
    fm = ""; body = raw;
    if (!raw.StartsWith("---")) return;
    var lines = raw.Split('\n');
    for (var i = 1; i < lines.Length; i++)
    {
        if (lines[i].Trim() == "---")
        {
            fm = string.Join("\n", lines[1..i]);
            body = string.Join("\n", lines[(i + 1)..]);
            return;
        }
    }
}

static void WriteAtomXml(string path, SiteConfig site, List<BlogPost> posts)
{
    var sb = new StringBuilder();
    sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
    sb.Append("<feed xmlns=\"http://www.w3.org/2005/Atom\">\n");
    sb.Append($"  <title>{Xml(site.Title)}</title>\n");
    sb.Append($"  <link href=\"{site.Url}/atom.xml\" rel=\"self\" />\n");
    sb.Append($"  <link href=\"{site.Url}/\" />\n");
    sb.Append($"  <id>{site.Url}/</id>\n");
    sb.Append($"  <updated>{(posts.Count > 0 ? posts[0].Updated : FormatUtc(DateTime.UtcNow))}</updated>\n");
    if (!string.IsNullOrWhiteSpace(site.Author))
        sb.Append($"  <author><name>{Xml(site.Author)}</name></author>\n");
    foreach (var p in posts)
    {
        sb.Append("  <entry>\n");
        sb.Append($"    <title>{Xml(p.Title)}</title>\n");
        sb.Append($"    <link href=\"{p.Permalink}\" />\n");
        sb.Append($"    <id>{p.Permalink}</id>\n");
        sb.Append($"    <updated>{p.Updated}</updated>\n");
        sb.Append($"    <summary>{Xml(p.Excerpt)}</summary>\n");
        sb.Append($"    <content type=\"html\">{Xml(p.Content)}</content>\n");
        sb.Append("  </entry>\n");
    }
    sb.Append("</feed>\n");
    File.WriteAllText(path, sb.ToString());
}

static void WriteSitemapXml(string path, SiteConfig site, List<BlogPost> posts)
{
    var sb = new StringBuilder();
    sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
    sb.Append("<urlset xmlns=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n");
    sb.Append($"  <url><loc>{site.Url}/</loc></url>\n");
    foreach (var p in posts)
        sb.Append($"  <url><loc>{p.Permalink}</loc><lastmod>{p.Updated}</lastmod></url>\n");
    sb.Append("</urlset>\n");
    File.WriteAllText(path, sb.ToString());
}

static void WriteSearchXml(string path, List<BlogPost> posts)
{
    var sb = new StringBuilder();
    sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<search>\n");
    foreach (var p in posts)
    {
        sb.Append("  <entry>\n");
        sb.Append($"    <title>{Xml(p.Title)}</title>\n");
        sb.Append($"    <url>/post/{p.Slug}/</url>\n");
        sb.Append($"    <content>{Xml(MakeExcerpt(p.Content, 4096))}</content>\n");
        foreach (var t in p.Tags) sb.Append($"    <tags>{Xml(t)}</tags>\n");
        foreach (var c in p.Categories) sb.Append($"    <categories>{Xml(c)}</categories>\n");
        sb.Append("  </entry>\n");
    }
    sb.Append("</search>\n");
    File.WriteAllText(path, sb.ToString());
}

static string Xml(string s) => s
    .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

static void CopyDir(string src, string dst)
{
    Directory.CreateDirectory(dst);
    foreach (var f in Directory.GetFiles(src))
        File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
    foreach (var d in Directory.GetDirectories(src))
        CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
}

static (string input, string output, string site, string images) ParseArgs(string[] args)
{
    var input = "./posts";
    var output = "./public";
    var site = "./site.json";
    var images = "";
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--input": input = args[++i]; break;
            case "--output": output = args[++i]; break;
            case "--site": site = args[++i]; break;
            case "--images": images = args[++i]; break;
        }
    }
    return (input, output, site, images);
}

[DoesNotReturn]
static void Fail(string msg)
{
    Console.Error.WriteLine("✗ " + msg);
    Environment.Exit(1);
}
