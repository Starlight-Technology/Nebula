namespace Nebula.Agent;

public class Command
{
    public int Id { get; set; }
    public string Objective { get; set; } = string.Empty;
    public string Run { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
}
