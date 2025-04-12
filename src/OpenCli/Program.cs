using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

// Populate values from your OpenAI deployment
var modelId = "o3-mini";

Console.WriteLine("Welcome to the OpenCLI Assistant!");
Console.WriteLine("This is a command line assistant that can execute commands on your system.");

// Get the version of the app
var version = Assembly.GetExecutingAssembly().GetName().Version;
Console.WriteLine($"Version: {version}");
Console.WriteLine();


string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

// Check if the variable is set or null
if (string.IsNullOrEmpty(apiKey))
{
    Console.WriteLine("OPENAI_KEY is not set.");
    Console.Write("Please enter your OpenAI key: ");
    apiKey = Console.ReadLine();
    if (string.IsNullOrEmpty(apiKey))
    {
        Console.WriteLine("No key provided. Exiting.");
        return;
    }

    try
    {
        Environment.SetEnvironmentVariable("OPENAI_KEY", apiKey);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Unable to set the OPENAI_KEY as environmental variable. Error: " + ex.Message);
        return;
    }
}

// Create a kernel with Azure OpenAI chat completion
var builder = Kernel
    .CreateBuilder()
    .AddOpenAIChatCompletion(modelId, apiKey);

builder.Services.Configure<JsonSerializerOptions>(options => {
    options.TypeInfoResolver = new CliPluginJsonTypeResolver();
    options.TypeInfoResolverChain.Add(AppJsonSerializerContext.Default);
});

// Add enterprise components
builder.Services.AddLogging(services => services.SetMinimumLevel(LogLevel.Trace));

// Build the kernel
Kernel kernel = builder.Build();
var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

kernel.Plugins.AddFromType<CliPlugin>("cli");

// Enable planning
OpenAIPromptExecutionSettings openAIPromptExecutionSettings = new()
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

// Create a history store the conversation
var history = new ChatHistory();

var reducer = new ChatHistoryTruncationReducer(targetCount: 10); // Keep system message and last user message


history.AddSystemMessage($"""
    You are an helpful assistant of the user working with a shell in {RuntimeInformation.OSDescription}. 
    Help user in issue commands in powershell or cmd or bash etc, for example you can execute programs, check if a program is installed or execute powershell commands.
    You'll be provided with functions to execute commands on the system. You can get or set the current directory with the specific function.
    When you execute a command, if you believe the command will change the system, you must ask for user approval before executing it.
    Before executing a command briefly explain what the command does and why you are executing it.
    Avoid to be be prosaic with things like "Let me know how you'd like to proceed next."
    """);

// Initiate a back-and-forth chat
string? userInput;

Console.WriteLine("Hello! I'm your command line assistant. How can I help you today? (CTRL+C to quit)");
Console.WriteLine();

do
{
    // Collect user input
    Console.Write("You > ");
    userInput = Console.ReadLine();

    if (userInput == null)
    {
        break;
    }

    if (string.IsNullOrWhiteSpace(userInput))
    {
        // Skip empty input
        continue;
    }

    // Add user input
    history.AddUserMessage(userInput);

    var reducedMessages = await reducer.ReduceAsync(history);

    if (reducedMessages is not null)
    {
        history = new ChatHistory(reducedMessages);
    }

    // Get the response from the AI
    var result = await chatCompletionService.GetChatMessageContentAsync(
        history,
        executionSettings: openAIPromptExecutionSettings,
        kernel: kernel);

    // Print the results
    Console.WriteLine("AI > " + result);

    // Add the message from the agent to the chat history
    history.AddMessage(result.Role, result.Content ?? string.Empty);
} while (userInput is not null);


public class CliPlugin
{
    [KernelFunction("exe")]
    [Description("Execute a cli command, the targetFilePath could be full path or an exe like git, powershell.exe, etc")]
    public async Task<string> ExecuteCommand(
        string targetFilePath,
        string[] arguments
    )
    {
        Console.WriteLine($"OS > Executing {targetFilePath} {string.Join(" ", arguments)}...");

        var result = await Cli.Wrap(targetFilePath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .WithWorkingDirectory(Environment.CurrentDirectory)
            .ExecuteBufferedAsync();

        if (result.IsSuccess)
        {
            return result.StandardOutput;
        }

        return result.StandardError;
    }

    [KernelFunction("cur_dir")]
    [Description("Get or change the current directory")]
    public string GetOrChangeCurrentDirectory(string? newDirectory)
    {
        if (string.IsNullOrEmpty(newDirectory))
        {
            return Environment.CurrentDirectory;
        }
        Environment.CurrentDirectory = newDirectory;
        return Environment.CurrentDirectory;
    }
}

public class CliPluginJsonTypeResolver : IJsonTypeInfoResolver
{
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        if (type == typeof(string))
        {
            return JsonMetadataServices.CreateValueInfo<string>(
                options,
                JsonMetadataServices.StringConverter);
        }
        if (type == typeof(string[]))
        {
            var elementTypeInfo = JsonMetadataServices.CreateValueInfo<string>(
                options,
                JsonMetadataServices.StringConverter);

            return JsonMetadataServices.CreateArrayInfo(
                options,
                new JsonCollectionInfoValues<string[]>
                {
                    ObjectCreator = () => []
                });
        }
        if (type == typeof(Task<string>))
        {
            // For Task<T>, you'll need to register a custom converter
            // This is a simplified approach - you may need to customize this based on your needs
            return JsonMetadataServices.CreateValueInfo<Task<string>>(options, new TaskStringConverter());
        }
        return null;
    }

    // Specialized converter just for Task<string>
    private class TaskStringConverter : JsonConverter<Task<string?>>
    {
        public override Task<string?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return Task.FromResult<string?>(null);
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected string value for Task<string>");
            }

            string? value = reader.GetString();
            return Task.FromResult(value);
        }

        public override void Write(Utf8JsonWriter writer, Task<string?> value, JsonSerializerOptions options)
        {
            if (value == null || value.Result == null)
            {
                writer.WriteNullValue();
                return;
            }

            // Ensure the task is completed
            if (!value.IsCompleted)
            {
                value.Wait();
            }

            writer.WriteStringValue(value.Result);
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Task<string>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
    // The partial keyword is important - the compiler will generate the implementation
}