namespace ClientPrefs_GoldKingZ.Shared;

public class MySqlServer
{
    public string Server   { get; set; } = "localhost";
    public int    Port     { get; set; } = 3306;
    public string Database { get; set; } = "MySql_Database";
    public string Username { get; set; } = "MySql_Username";
    public string Password { get; set; } = "MySql_Password";
}

public class MySqlConfig
{
    public string Server   { get; set; } = "";
    public int    Port     { get; set; } = 3306;
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public List<MySqlServer>? MySql_Servers { get; set; }

    public IReadOnlyList<MySqlServer> EffectiveServers
    {
        get
        {
            if (MySql_Servers is { Count: > 0 }) return MySql_Servers;
            if (!string.IsNullOrEmpty(Server))
            {
                return new[]
                {
                    new MySqlServer
                    {
                        Server   = Server,
                        Port     = Port,
                        Database = Database,
                        Username = Username,
                        Password = Password,
                    }
                };
            }
            return Array.Empty<MySqlServer>();
        }
    }
}
