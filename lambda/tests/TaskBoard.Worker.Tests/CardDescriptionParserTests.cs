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
}
