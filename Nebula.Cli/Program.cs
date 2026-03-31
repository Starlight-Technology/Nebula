using Microsoft.Extensions.DependencyInjection;

using Nebula.Agent;
using Nebula.Llama.Client;
using Nebula.Runner;


var services = new ServiceCollection();

// registre suas interfaces aqui
services.AddSingleton<ILlamaClient, LlamaClient>();
services.AddSingleton<IShellExecutor, ShellExecutor>();
services.AddSingleton<IManager, Manager>();

var provider = services.BuildServiceProvider();

// resolva o serviço principal
var manager = provider.GetRequiredService<IManager>();

Console.WriteLine("Starting LLM");
var response = await manager.ManageResponse("Hello");

Console.WriteLine(response);
Console.WriteLine("LLM OK");

Console.WriteLine("Starting LLM");
response = await manager.ManageResponse("list files on c");

Console.WriteLine(response);

while (true)
{
    var prompt = Console.ReadLine();
    if (string.IsNullOrEmpty(prompt))
    {
        continue;
    }

    response = await manager.ManageResponse(prompt);

    Console.WriteLine(response);

}

