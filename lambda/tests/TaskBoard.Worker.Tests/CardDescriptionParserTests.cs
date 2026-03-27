using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests;

public class CardDescriptionParserTests
{
    [Fact]
    public void ParseSections_WellFormedSections_ReturnsCorrectDictionary()
    {
        var description = "# Requirements\nSome requirements\n\n# Design\nSome design";

        var sections = CardDescriptionParser.ParseSections(description);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Some requirements", sections["Requirements"]);
        Assert.Equal("Some design", sections["Design"]);
    }

    [Fact]
    public void ParseSections_NoSections_ReturnsEmptyDictionary()
    {
        var description = "Just plain text with no headings";

        var sections = CardDescriptionParser.ParseSections(description);

        Assert.Empty(sections);
    }

    [Fact]
    public void ParseSections_EmptyString_ReturnsEmptyDictionary()
    {
        var sections = CardDescriptionParser.ParseSections("");

        Assert.Empty(sections);
    }

    [Fact]
    public void ParseSections_SectionWithNoBody_ReturnsEmptyBody()
    {
        var description = "# EmptySection";

        var sections = CardDescriptionParser.ParseSections(description);

        Assert.Single(sections);
        Assert.Equal(string.Empty, sections["EmptySection"]);
    }

    [Fact]
    public void MergeSections_UpdatesOnlyAllowedSections()
    {
        var existing = "# Requirements\nOld requirements\n\n# Design\nOld design\n\n# Test Plan\nOld tests";
        var updates = new Dictionary<string, string>
        {
            ["Requirements"] = "New requirements",
            ["Design"] = "New design",
            ["Test Plan"] = "New tests"
        };
        var allowed = new List<string> { "Requirements" };

        var result = CardDescriptionParser.MergeSections(existing, updates, allowed);

        Assert.Contains("# Requirements\nNew requirements", result);
        Assert.Contains("# Design\nOld design", result);
        Assert.Contains("# Test Plan\nOld tests", result);
    }

    [Fact]
    public void MergeSections_PreservesSectionsNotInUpdates()
    {
        var existing = "# Requirements\nExisting requirements\n\n# Notes\nSome notes";
        var updates = new Dictionary<string, string>
        {
            ["Requirements"] = "Updated requirements"
        };
        var allowed = new List<string> { "Requirements", "Notes" };

        var result = CardDescriptionParser.MergeSections(existing, updates, allowed);

        Assert.Contains("# Requirements\nUpdated requirements", result);
        Assert.Contains("# Notes\nSome notes", result);
    }

    [Fact]
    public void MergeSections_AddsNewAllowedSection()
    {
        var existing = "# Requirements\nExisting requirements";
        var updates = new Dictionary<string, string>
        {
            ["Design"] = "New design content"
        };
        var allowed = new List<string> { "Requirements", "Design" };

        var result = CardDescriptionParser.MergeSections(existing, updates, allowed);

        Assert.Contains("# Requirements\nExisting requirements", result);
        Assert.Contains("# Design\nNew design content", result);
    }

    [Fact]
    public void ParseSections_WithDetailsBlocks_IncludesDetailsInSectionBody()
    {
        var description =
            "# Technical Design\n" +
            "<details><summary>Click to expand full technical design</summary>\n\n" +
            "The detailed design content here.\n\n" +
            "</details>";

        var sections = CardDescriptionParser.ParseSections(description);

        Assert.Single(sections);
        Assert.True(sections.ContainsKey("Technical Design"));
        Assert.Contains("<details>", sections["Technical Design"]);
        Assert.Contains("The detailed design content here.", sections["Technical Design"]);
        Assert.Contains("</details>", sections["Technical Design"]);
    }

    [Fact]
    public void ParseSections_DesignReviewSummarySection_ParsedCorrectly()
    {
        var description =
            "# Design Review Summary\n" +
            "## Approach\n" +
            "Some approach description.\n\n" +
            "# Technical Design\n" +
            "Technical details here.";

        var sections = CardDescriptionParser.ParseSections(description);

        Assert.Equal(2, sections.Count);
        Assert.True(sections.ContainsKey("Design Review Summary"));
        Assert.Contains("## Approach", sections["Design Review Summary"]);
        Assert.Contains("Some approach description.", sections["Design Review Summary"]);
        Assert.True(sections.ContainsKey("Technical Design"));
        Assert.Contains("Technical details here.", sections["Technical Design"]);
    }

    [Fact]
    public void ParseSections_MixedCollapsedAndOpenSections_AllParsed()
    {
        var description =
            "# Design Review Summary\n" +
            "## Approach\nHigh-level approach.\n\n" +
            "---\n\n" +
            "# Technical Design\n" +
            "<details><summary>Click to expand full technical design</summary>\n\n" +
            "Full technical design content.\n\n" +
            "</details>\n\n" +
            "# Decisions\n" +
            "<details><summary>Click to expand decision log</summary>\n\n" +
            "Decision log content.\n\n" +
            "</details>";

        var sections = CardDescriptionParser.ParseSections(description);

        Assert.Equal(3, sections.Count);
        Assert.True(sections.ContainsKey("Design Review Summary"));
        Assert.True(sections.ContainsKey("Technical Design"));
        Assert.True(sections.ContainsKey("Decisions"));
        Assert.Contains("High-level approach.", sections["Design Review Summary"]);
        Assert.Contains("<details>", sections["Technical Design"]);
        Assert.Contains("Full technical design content.", sections["Technical Design"]);
        Assert.Contains("Decision log content.", sections["Decisions"]);
    }
}
