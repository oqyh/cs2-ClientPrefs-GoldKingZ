namespace ClientPrefs_GoldKingZ.Shared;

public enum PrefsAPI_SaveMode
{
    Disabled = 0,
    OnPlayerDisconnect = 1,
    OnMapEnd = 2,
}

public sealed class ClientPrefsOptions
{
    public PrefsAPI_SaveMode PrefsAPI_CookiesEnable { get; set; } = PrefsAPI_SaveMode.Disabled;
    public int PrefsAPI_CookiesAutoRemoveInactivePlayersOlderThanDays { get; set; } = 7;

    public PrefsAPI_SaveMode PrefsAPI_MySqlEnable { get; set; } = PrefsAPI_SaveMode.Disabled;
    public int PrefsAPI_MySqlConnectionTimeout { get; set; } = 30;
    public int PrefsAPI_MySqlRetryAttempts     { get; set; } = 3;
    public int PrefsAPI_MySqlRetryDelay        { get; set; } = 2;
    public int PrefsAPI_MySqlAutoRemoveInactivePlayersOlderThanDays { get; set; } = 7;

    public MySqlConfig PrefsAPI_MySqlConfig { get; set; } = new();

    public string? PrefsAPI_MySqlTableName { get; set; }

    public bool PrefsAPI_ReloadOnReconnect { get; set; } = false;
    public bool PrefsAPI_LoadDefaultAfterDrop { get; set; } = true;
    public bool PrefsAPI_DebugEnable { get; set; } = false;
}