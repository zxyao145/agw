namespace Agw.A2A;

public class A2AServerOptions
{
    private string _prefix = "/api/a2a";
    public string Prefix
    {
        get => _prefix;
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                _prefix = "/";
            }
            else if (value.EndsWith("/"))
            {
                _prefix = value;
            }
            else
            {
                _prefix = value + "/";
            }
        }
    }
}
