namespace TradingAgent.Config;

public class AhkConfig
{
    public const string SectionName = "Ahk";

    public string PortalUrl { get; set; } = "https://www.ahktrading.com";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string TradingPin { get; set; } = "";
    public int DefaultQty { get; set; } = 100;
    public decimal MaxOrderValuePkr { get; set; } = 50_000m;
    public string SessionDir { get; set; } = "session_ahk";
    public string LogDir { get; set; } = "logs/trading";
}
