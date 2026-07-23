using AgentFox.Modules.Web;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class WebTodoProjectionTests
{
    [TestMethod]
    public void TodoProjection_ReturnsOnlySafeDisplayFields()
    {
        const string state =
            """
            {"savedAt":"2026-07-23T00:00:00Z","stateBag":{
              "TodoProvider":{"items":[
                {"id":1,"title":"Inspect UI","isComplete":true,"internal":"hidden"},
                {"id":2,"title":"Run checks","isComplete":false}
              ],"nextId":3},
              "ChatHistoryProvider":{"messages":["must not leak"]}
            }}
            """;

        var items = WebModule.ReadTodoItems(state);

        Assert.AreEqual(2, items.Count);
        Assert.AreEqual("Inspect UI", items[0].Title);
        Assert.IsTrue(items[0].Completed);
        Assert.AreEqual("Run checks", items[1].Title);
        Assert.IsFalse(items[1].Completed);
    }

    [TestMethod]
    public void TodoProjection_FailsSoftForMalformedState()
    {
        Assert.AreEqual(0, WebModule.ReadTodoItems("not-json").Count);
        Assert.AreEqual(0, WebModule.ReadTodoItems(null).Count);
    }
}
