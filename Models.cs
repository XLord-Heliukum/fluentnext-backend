namespace FluentNext.Backend;

// 以下模型字段名与前端 FluentNext.Frontend/Models/Models.cs 严格 1:1 对应，
// JSON 序列化统一走 camelCase（System.Text.Json 默认 PascalCase，需在序列化处显式配置）。

/// <summary>content.json 顶层对象（api/content.json）。</summary>
public sealed class ContentData
{
    public SiteInfo Site { get; set; } = new();
    public List<BlogPost> Posts { get; set; } = new();
    public List<CategoryInfo> Categories { get; set; } = new();
    public List<TagInfo> Tags { get; set; } = new();
    public List<ArchiveInfo> Archives { get; set; } = new();
}

public sealed class SiteInfo
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public List<MenuLink> Menu { get; set; } = new();
}

public sealed class MenuLink
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class BlogPost
{
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Permalink { get; set; } = "";
    public string Date { get; set; } = "";      // 格式 2026-09-12T12:00:00.000Z (UTC, 含毫秒)
    public string Updated { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public string Content { get; set; } = "";    // 渲染后的 HTML
    public List<TocItem> Toc { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}

public sealed class TocItem
{
    public int Level { get; set; }
    public string Text { get; set; } = "";
    public string Id { get; set; } = "";
}

public sealed class CategoryInfo
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public int Count { get; set; }
}

public sealed class TagInfo
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public int Count { get; set; }
}

public sealed class ArchiveInfo
{
    public int Year { get; set; }
    public int Count { get; set; }
    public List<ArchiveMonth> Months { get; set; } = new();
}

public sealed class ArchiveMonth
{
    public int Month { get; set; }
    public int Count { get; set; }
}

/// <summary>site.json 站点配置（author 只用于 RSS，不进 content.json 的 site）。</summary>
public sealed class SiteConfig
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public string Author { get; set; } = "";
    public List<MenuLink> Menu { get; set; } = new();
}
