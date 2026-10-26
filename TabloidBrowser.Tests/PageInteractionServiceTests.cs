using Xunit;

namespace TabloidBrowser.Tests;

public class PageInteractionServiceTests
{
    private (PageInteractionService service, List<string> jsCalls) CreateService(string? jsResult = null)
    {
        var jsCalls = new List<string>();
        string? lastJs = null;

        Task<string?> ExecuteJs(string script)
        {
            jsCalls.Add(script);
            lastJs = script;
            return Task.FromResult(jsResult);
        }

        Task WaitForNav() => Task.CompletedTask;

        var service = new PageInteractionService(ExecuteJs, WaitForNav);
        return (service, jsCalls);
    }

    [Fact]
    public async Task GetCurrentUrlAsync_ExecutesJs()
    {
        var (service, jsCalls) = CreateService("\"https://example.com\"");
        var url = await service.GetCurrentUrlAsync();
        Assert.Equal("https://example.com", url);
        Assert.Single(jsCalls);
        Assert.Contains("window.location.href", jsCalls[0]);
    }

    [Fact]
    public async Task ClickElementAsync_ReturnsTrue_WhenElementClicked()
    {
        var (service, jsCalls) = CreateService("true");
        var result = await service.ClickElementAsync("#submit-btn");
        Assert.True(result);
        Assert.Contains("submit-btn", jsCalls[0]);
    }

    [Fact]
    public async Task ClickElementAsync_ReturnsTrue_WhenResultIsJsonQuoted()
    {
        var (service, _) = CreateService("\"true\"");
        var result = await service.ClickElementAsync("#submit-btn");
        Assert.True(result);
    }

    [Fact]
    public async Task ClickElementAsync_ReturnsFalse_WhenElementNotFound()
    {
        var (service, _) = CreateService("false");
        var result = await service.ClickElementAsync(".nonexistent");
        Assert.False(result);
    }

    [Fact]
    public async Task ClickElementAsync_ReturnsFalse_WhenResultIsJsonQuoted()
    {
        var (service, _) = CreateService("\"false\"");
        var result = await service.ClickElementAsync(".nonexistent");
        Assert.False(result);
    }

    [Fact]
    public async Task FillFieldAsync_ReturnsTrue_WhenFieldFilled()
    {
        var (service, jsCalls) = CreateService("true");
        var result = await service.FillFieldAsync("#email", "test@example.com");
        Assert.True(result);
        Assert.Contains("email", jsCalls[0]);
        Assert.Contains("test@example.com", jsCalls[0]);
    }

    [Fact]
    public async Task FillFieldAsync_ReturnsFalse_WhenFieldNotFound()
    {
        var (service, _) = CreateService("false");
        var result = await service.FillFieldAsync(".missing", "value");
        Assert.False(result);
    }

    [Fact]
    public async Task FillFieldAsync_ReturnsTrue_WhenResultIsJsonQuoted()
    {
        var (service, _) = CreateService("\"true\"");
        var result = await service.FillFieldAsync("#email", "test@example.com");
        Assert.True(result);
    }

    [Fact]
    public async Task GetInteractiveElementsAsync_ReturnsElements()
    {
        var json = "[{\"tag\":\"input\",\"type\":\"text\",\"text\":\"\",\"placeholder\":\"Email\",\"name\":\"email\",\"id\":\"email\",\"href\":\"\",\"selector\":\"#email\"}]";
        var (service, _) = CreateService(json);
        var elements = await service.GetInteractiveElementsAsync();
        Assert.Single(elements);
        Assert.Equal("input", elements[0].Tag);
        Assert.Equal("#email", elements[0].Selector);
    }

    [Fact]
    public async Task GetInteractiveElementsAsync_ReturnsEmpty_WhenNoElements()
    {
        var (service, _) = CreateService("[]");
        var elements = await service.GetInteractiveElementsAsync();
        Assert.Empty(elements);
    }

    [Fact]
    public async Task GetInteractiveElementsAsync_ReturnsEmpty_WhenJsonInvalid()
    {
        var (service, _) = CreateService("not-json");
        var elements = await service.GetInteractiveElementsAsync();
        Assert.Empty(elements);
    }

    [Fact]
    public async Task GetSelectedTextAsync_ReturnsSelectedText()
    {
        var (service, _) = CreateService("\"hello world\"");
        var text = await service.GetSelectedTextAsync();
        Assert.Equal("hello world", text);
    }

    [Fact]
    public async Task GetSelectedTextAsync_ReturnsEmpty_WhenNothingSelected()
    {
        var (service, _) = CreateService("null");
        var text = await service.GetSelectedTextAsync();
        Assert.Equal("", text);
    }

    [Fact]
    public async Task ClickElementAsync_EscapesSelector()
    {
        var (service, jsCalls) = CreateService("true");
        await service.ClickElementAsync("div[class='test']");
        Assert.Contains("div[class=\\'test\\']", jsCalls[0]);
    }

    [Fact]
    public async Task ScrollPageAsync_Down_ReturnsTrue()
    {
        var (service, jsCalls) = CreateService("undefined");
        var result = await service.ScrollPageAsync("down");
        Assert.True(result);
        Assert.Contains("scrollBy(0, 500)", jsCalls[0]);
    }

    [Fact]
    public async Task ScrollPageAsync_Up_ReturnsTrue()
    {
        var (service, jsCalls) = CreateService("undefined");
        var result = await service.ScrollPageAsync("up");
        Assert.True(result);
        Assert.Contains("scrollBy(0, -500)", jsCalls[0]);
    }

    [Fact]
    public async Task ScrollPageAsync_Top_ReturnsTrue()
    {
        var (service, jsCalls) = CreateService("undefined");
        var result = await service.ScrollPageAsync("top");
        Assert.True(result);
        Assert.Contains("scrollTo(0, 0)", jsCalls[0]);
    }

    [Fact]
    public async Task ScrollPageAsync_Bottom_ReturnsTrue()
    {
        var (service, jsCalls) = CreateService("undefined");
        var result = await service.ScrollPageAsync("bottom");
        Assert.True(result);
        Assert.Contains("document.body.scrollHeight", jsCalls[0]);
    }

    [Fact]
    public async Task ScrollPageAsync_CustomAmount_UsesAmount()
    {
        var (service, jsCalls) = CreateService("undefined");
        var result = await service.ScrollPageAsync("down", 1000);
        Assert.True(result);
        Assert.Contains("scrollBy(0, 1000)", jsCalls[0]);
    }

    [Fact]
    public async Task ScrollPageAsync_InvalidDirection_ReturnsFalse()
    {
        var (service, _) = CreateService("undefined");
        var result = await service.ScrollPageAsync("diagonal");
        Assert.False(result);
    }

    [Fact]
    public async Task PressKeyAsync_Enter_ReturnsTrue()
    {
        var (service, jsCalls) = CreateService("true");
        var result = await service.PressKeyAsync("Enter");
        Assert.True(result);
        Assert.Contains("keydown", jsCalls[0]);
        Assert.Contains("Enter", jsCalls[0]);
    }

    [Fact]
    public async Task PressKeyAsync_Tab_ReturnsTrue()
    {
        var (service, jsCalls) = CreateService("true");
        var result = await service.PressKeyAsync("Tab");
        Assert.True(result);
        Assert.Contains("Tab", jsCalls[0]);
    }

    [Fact]
    public async Task PressKeyAsync_Escape_ReturnsTrue()
    {
        var (service, jsCalls) = CreateService("true");
        var result = await service.PressKeyAsync("Escape");
        Assert.True(result);
        Assert.Contains("Escape", jsCalls[0]);
    }

    [Fact]
    public async Task PressKeyAsync_UnknownKey_ReturnsFalse()
    {
        var (service, _) = CreateService("false");
        var result = await service.PressKeyAsync("F24");
        Assert.False(result);
    }

    [Fact]
    public async Task PressKeyAsync_DispatchesKeydownAndKeyup()
    {
        var (service, jsCalls) = CreateService("true");
        await service.PressKeyAsync("Enter");
        Assert.Contains("keydown", jsCalls[0]);
        Assert.Contains("keyup", jsCalls[0]);
    }

    [Fact]
    public async Task GoBackAsync_ReturnsTrue()
    {
        var (service, jsCalls) = CreateService("undefined");
        var result = await service.GoBackAsync();
        Assert.True(result);
        Assert.Contains("history.back()", jsCalls[0]);
    }

    [Fact]
    public async Task GetInteractiveElementsAsync_GeneratesSelectorWithPlaceholderFallback()
    {
        var (service, jsCalls) = CreateService("[]");
        await service.GetInteractiveElementsAsync();
        Assert.Contains("placeholder", jsCalls[0]);
        Assert.Contains("selector", jsCalls[0]);
        Assert.Contains("CSS.escape(el.id)", jsCalls[0]);
    }

    [Fact]
    public async Task GetInteractiveElementsAsync_SelectorFallsBackToPlaceholderAndType()
    {
        var (service, jsCalls) = CreateService("[]");
        await service.GetInteractiveElementsAsync();
        var js = jsCalls[0];
        Assert.Contains("[placeholder", js);
        Assert.Contains("[type", js);
    }
}
