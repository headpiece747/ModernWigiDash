namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the icon picker's decision model at its interface, over the real
/// Griddy catalog and without a picker window: the search filter, the
/// selection + highlight, the custom chip text (including that it follows
/// the selection), and the accept verdict.
/// </summary>
[TestClass]
public class IconPickerModelTests
{
    [TestMethod]
    public void Ctor_NamedSeed_HighlightsTheIconAndLeavesTheChipEmpty()
    {
        string name = GriddyIcons.Names.First();
        var model = new IconPickerModel(name);

        Assert.AreEqual(name, model.Chosen);
        Assert.IsTrue(model.IsHighlighted(name));
        Assert.IsFalse(model.IsHighlighted(name + "-nope"));
        Assert.AreEqual("", model.ChipText);
    }

    [TestMethod]
    public void Ctor_CustomSeed_SetsTheCustomChip()
    {
        var model = new IconPickerModel("icons/cool.svg");

        Assert.AreEqual("Custom: icons/cool.svg", model.ChipText);
        Assert.IsTrue(model.IsHighlighted("icons/cool.svg"));
    }

    [TestMethod]
    public void UpdateSearch_BlankOrWhitespaceFilter_ShowsEveryCatalogName()
    {
        var model = new IconPickerModel(null);

        model.UpdateSearch("");
        Assert.AreEqual(GriddyIcons.Names.Count, model.VisibleNames.Count);

        model.UpdateSearch("   ");
        Assert.AreEqual(GriddyIcons.Names.Count, model.VisibleNames.Count);

        model.UpdateSearch(null);
        Assert.AreEqual(GriddyIcons.Names.Count, model.VisibleNames.Count);
    }

    [TestMethod]
    public void UpdateSearch_CaseInsensitiveFilter_ShowsOnlyMatchingNames()
    {
        string name = GriddyIcons.Names.First();
        var model = new IconPickerModel(null);

        model.UpdateSearch(name.ToLowerInvariant());
        Assert.IsTrue(model.VisibleNames.Contains(name));

        model.UpdateSearch(char.ToUpperInvariant(name[0]) + name[1..]);
        Assert.IsTrue(model.VisibleNames.Contains(name));

        model.UpdateSearch("zzz-no-such-icon");
        Assert.AreEqual(0, model.VisibleNames.Count);
    }

    [TestMethod]
    public void Select_CaseInsensitiveHighlight_FollowsTheSelection()
    {
        var model = new IconPickerModel(null);
        string name = GriddyIcons.Names.First();

        model.Select(name);

        Assert.AreEqual(name, model.Chosen);
        Assert.IsTrue(model.IsHighlighted(name.ToLowerInvariant()));
        Assert.IsFalse(model.IsHighlighted(GriddyIcons.Names.First(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase))));
    }

    [TestMethod]
    public void Select_CustomPath_SetsTheCustomChipText()
    {
        var model = new IconPickerModel(null);

        model.Select("icons/arrow.svg");

        Assert.AreEqual("Custom: icons/arrow.svg", model.ChipText);
    }

    [TestMethod]
    public void Select_NamedIconAfterCustom_ClearsTheStaleChip()
    {
        var model = new IconPickerModel("icons/stale.svg");
        string name = GriddyIcons.Names.First();

        model.Select(name);

        Assert.AreEqual("", model.ChipText);
        Assert.AreEqual(name, model.Chosen);
    }

    [TestMethod]
    public void CustomChipText_IsTheOneSpelling()
    {
        Assert.AreEqual("Custom: icons/x.svg", IconPickerModel.CustomChipText("icons/x.svg"));
    }

    [TestMethod]
    public void Accept_BlankChoice_ReturnsNull()
    {
        var model = new IconPickerModel(null);

        Assert.IsNull(model.Accept());

        model.Select("");
        Assert.IsNull(model.Accept());
    }

    [TestMethod]
    public void Accept_NamedAndCustom_ReturnsTheChosenValue()
    {
        var model = new IconPickerModel(null);
        string name = GriddyIcons.Names.First();

        model.Select(name);
        Assert.AreEqual(name, model.Accept());

        model.Select("icons/arrow.svg");
        Assert.AreEqual("icons/arrow.svg", model.Accept());
    }

    [TestMethod]
    public void Accept_CustomFilePath_AcceptsAsIs()
    {
        var model = new IconPickerModel(null);

        model.Select("C:/Users/test/icons/custom.svg");
        Assert.AreEqual("C:/Users/test/icons/custom.svg", model.Accept());

        model.Select("/home/user/icons/other.svg");
        Assert.AreEqual("/home/user/icons/other.svg", model.Accept());
    }

    [TestMethod]
    public void Accept_WhitespaceOnly_ReturnsNull()
    {
        var model = new IconPickerModel(null);

        model.Select("   ");
        Assert.IsNull(model.Accept());

        model.Select("\t\n");
        Assert.IsNull(model.Accept());
    }

    [TestMethod]
    public void BrowseSvg_ValidFile_CopiesAndSelectsIt()
    {
        var model = new IconPickerModel(null);
        string copied = "icons/copied.svg";

        var verdict = model.BrowseSvg("C:/src/picked.svg", _ => true, _ => copied);

        Assert.IsTrue(verdict.Accepted);
        Assert.AreEqual(copied, verdict.SelectedPath);
        Assert.IsNull(verdict.RefusalMessage);
        // The accepted file becomes the selection (the chip follows it).
        Assert.AreEqual(copied, model.Chosen);
        Assert.AreEqual($"Custom: {copied}", model.ChipText);
    }

    [TestMethod]
    public void BrowseSvg_UnsupportedFile_RefusesWithoutCopying()
    {
        var model = new IconPickerModel(null);
        bool copyCalled = false;

        var verdict = model.BrowseSvg("C:/src/bad.svg", _ => false, _ => { copyCalled = true; return "icons/x.svg"; });

        Assert.IsFalse(verdict.Accepted);
        Assert.IsNull(verdict.SelectedPath);
        Assert.AreEqual(IconPickerModel.UnsupportedSvgMessage, verdict.RefusalMessage);
        Assert.IsFalse(copyCalled, "an unsupported file must never be copied into the icons folder");
        // The selection is untouched by a refused browse.
        Assert.AreEqual("", model.Chosen);
    }
}
