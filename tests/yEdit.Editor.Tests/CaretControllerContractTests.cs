using System.Reflection;

namespace yEdit.Editor.Tests;

/// <summary>
/// Phase 3 Task 3b の契約テスト: <c>_caret</c>/<c>_anchor</c>/<c>_desiredXpx</c> の所有権が
/// <c>EditorControl</c> から <c>CaretController</c> へ完全移譲されたことを reflection で確認する。
/// </summary>
public class CaretControllerContractTests
{
    [Fact]
    public void CaretController_Fields_AreOwnedByController()
    {
        var editorFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        string[] removed = { "_caret", "_anchor", "_desiredXpx" };
        foreach (var name in removed)
        {
            var f = typeof(EditorControl).GetField(name, editorFlags);
            Assert.Null(f);
        }

        var ctrlFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        var ctrlType = typeof(EditorControl).Assembly.GetType("yEdit.Editor.CaretController");
        Assert.NotNull(ctrlType);
        foreach (var name in removed)
        {
            var f = ctrlType!.GetField(name, ctrlFlags);
            Assert.NotNull(f);
        }
    }

    [Fact]
    public void EditorControl_HoldsCaretController_ByField()
    {
        var f = typeof(EditorControl).GetField("_caretCtrl",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
    }
}
