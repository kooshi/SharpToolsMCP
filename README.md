# SharpTools: Roslyn Powered C# Analysis & Modification MCP Server

SharpTools is a robust service designed to empower AI agents with advanced capabilities for understanding, analyzing, and modifying C# codebases. It leverages the .NET Compiler Platform (Roslyn) to provide deep static analysis and precise code manipulation, going far beyond simple text-based operations.

SharpTools is designed to give AI the same insights and tools a human developer relies on, leading to more intelligent and reliable code assistance. It is effectively a simple IDE, made for an AI user.

Due to the comprehensive nature of the suite, it can almost be used completely standalone for editing existing C# solutions. If you use the SSE server and port forward your router, I think it's even possible to have Claude's web chat ui connect to this and have it act as a full coding assistant.

## Prompts

The included [Identity Prompt](Prompts/identity.prompt) is my personal C# coding assistant prompt, and it works well in combination with this suite. You're welcome to use it as is, modify it to match your preferences, or omit it entirely.

In VS Code, set it as your `copilot-instructions.md` to have it included in every interaction.

The [Tool Use Prompt](Prompts/github-copilot-sharptools.prompt) is fomulated specifically for Github Copilot's Agent mode. It overrides specific sections within the Copilot Agent System Prompt so that it avoids the built in tools.

It is available as an MCP prompt as well, so within Copilot, you just need to type `/mcp`, and it should show up as an option.

Something similar will be necessary for other coding assistants to prevent them from using their own default editing tools.

I recommend crafting custom tool use prompts for each agent you use this with, based on their [individual system prompts](https://github.com/x1xhlol/system-prompts-and-models-of-ai-tools).

## Note

This is a personal project, slapped together over a few weeks, and built in no small part by its own tools. It generally works well as it is, but the code is still fairly ugly, and some of the features are still quirky (like removing newlines before and after overwritten members).

I intend to maintain and improve it for as long as I am using it, and I welcome feedback and contributions as a part of that.

## Features

*   **Dynamic Project Structure Mapping:** Generates a "map" of the solution, detailing namespaces and types, with complexity-adjusted resolution.
*   **Contextual Navigation Aids:** Provides simplified call graphs and dependency trees for local code understanding.
*   **Token Efficient Operation** Designed to provide only the highest signal context at every step to keep your agent on track longer without being overwhelmed or requiring summarization.
    *   All indentation is omitted in returned code, saving roughly 10% of tokens without affecting performance on the smartest models.
    *   FQN based navigation means the agent rarely needs to read unrelated code.
*   **FQN Fuzzy Matching:** Intelligently resolves potentially imprecise or incomplete Fully Qualified Names (FQNs) to exact Roslyn symbols.
*   **Comprehensive Source Resolution:** Retrieves source code for symbols from:
    *   Local solution files.
    *   External libraries via SourceLink.
    *   Embedded PDBs.
    *   Decompilation (ILSpy-based) as a fallback.
*   **Precise, Roslyn-Based Modifications:** Enables surgical code changes (add/overwrite/rename/move members, find/replace) rather than simple text manipulation.
*   **Automated Git Integration:**
    *   Creates dedicated, timestamped `sharptools/` branches for all modifications.
    *   Automatically commits every code change with a descriptive message.
    *   Offers a Git-powered `Undo` for the last modification.
*   **Concise AI Feedback Loop:**
    *   Confirms changes with precise diffs instead of full code blocks.
    *   Provides immediate, in-tool compilation error reports after modifications.
*   **Proactive Code Quality Analysis:**
    *   Detects and warns about high code complexity (cyclomatic, cognitive).
    *   Identifies semantically similar code to flag potential duplicates upon member addition.
*   **Broad Project Support:**
    *   Runs on Windows and Linux (and probably Mac)
    *   Can analyze projects targeting any .NET version, from Framework to Core to 5+
    *   Compatible with both modern SDK-style and legacy C# project formats.
    *   Respects `.editorconfig` settings for consistent code formatting.
*   **MCP Server Interface:** Exposes tools via Model Context Protocol (MCP) through:
    *   Server-Sent Events (SSE) for remote clients.
    *   Standard I/O (Stdio) for local process communication.

## Exposed Tools

SharpTools exposes a variety of "SharpTool_*" functions via MCP. Here's a brief overview categorized by their respective service files:

### Solution Tools

*   `SharpTool_LoadSolution`: Initializes the workspace with a given `.sln` file. This is the primary entry point.
*   `SharpTool_LoadProject`: Provides a detailed structural overview of a specific project within the loaded solution, including namespaces and types, to aid AI understanding of the project's layout.

### Analysis Tools

*   `SharpTool_GetMembers`: Lists members (methods, properties, etc.) of a type, including signatures and XML documentation.
*   `SharpTool_ViewDefinition`: Displays the source code of a symbol (class, method, etc.), including contextual information like call graphs or type references.
*   `SharpTool_ListImplementations`: Finds all implementations of an interface/abstract method or derived classes of a base class.
*   `SharpTool_FindReferences`: Locates all usages of a symbol across the solution, providing contextual code snippets.
*   `SharpTool_SearchDefinitions`: Performs a regex-based search across symbol declarations and signatures in both source code and compiled assemblies.
*   `SharpTool_ManageUsings`: Reads or overwrites using directives in a document.
*   `SharpTool_ManageAttributes`: Reads or overwrites attributes on a specific declaration.
*   `SharpTool_AnalyzeComplexity`: Performs complexity analysis (cyclomatic, cognitive, coupling, etc.) on methods, classes, or projects.
*   ~(Disabled) `SharpTool_GetAllSubtypes`: Recursively lists all nested members of a type.~
*   ~(Disabled) `SharpTool_ViewInheritanceChain`: Shows the inheritance hierarchy for a type.~
*   ~(Disabled) `SharpTool_ViewCallGraph`: Displays incoming and outgoing calls for a method.~
*   ~(Disabled) `SharpTool_FindPotentialDuplicates`: Finds semantically similar methods or classes.~

### Document Tools

*   `SharpTool_ReadRawFromRoslynDocument`: Reads the raw content of a file (indentation omitted).
*   `SharpTool_CreateRoslynDocument`: Creates a new file with specified content.
*   `SharpTool_OverwriteRoslynDocument`: Overwrites an existing file with new content.
*   `SharpTool_ReadTypesFromRoslynDocument`: Lists all types and their members defined within a specific source file.

### Modification Tools

*   `SharpTool_AddMember`: Adds a new member (method, property, field, nested type, etc.) to a specified type.
*   `SharpTool_OverwriteMember`: Replaces the definition of an existing member or type with new code, or deletes it.
*   `SharpTool_RenameSymbol`: Renames a symbol and updates all its references throughout the solution.
*   `SharpTool_FindAndReplace`: Performs regex-based find and replace operations within a specified symbol's declaration or across files matching a glob pattern.
*   `SharpTool_MoveMember`: Moves a member from one type/namespace to another.
*   `SharpTool_Undo`: Reverts the last applied change using Git integration.
*   ~(Disabled) `SharpTool_ReplaceAllReferences`: Replaces all references to a symbol with specified C# code.~

### Package Tools

*   ~(Disabled) `SharpTool_AddOrModifyNugetPackage`: Adds or updates a NuGet package reference in a project file.~

### Misc Tools

*   `SharpTool_RequestNewTool`: Allows the AI to request new tools or features, logging the request for human review.

## Prerequisites

*   .NET 8+ SDK for running the server
*   The .NET SDK of your target solution

## Building

To build the entire solution:
```bash
dotnet build SharpTools.sln
```
This will build all services and server applications.

## Running the Servers

### SSE Server (HTTP)

The SSE server hosts the tools on an HTTP endpoint.

```bash
# Navigate to the SseServer project directory
cd SharpTools.SseServer

# Run with default options (port 3001)
dotnet run

# Run with specific options
dotnet run -- --port 3005 --log-file ./logs/mcp-sse-server.log --log-level Debug
```
Key Options:
*   `--port <number>`: Port to listen on (default: 3001).
*   `--log-file <path>`: Path to a log file.
*   `--log-level <level>`: Minimum log level (Verbose, Debug, Information, Warning, Error, Fatal).
*   `--load-solution <path>`: Path to a `.sln` file to load on startup. Useful for manual testing. It is recommended to let the AI run the LoadSolution tool instead, as it returns some useful information.
*   `--build-configuration <config>`: Build configuration to use when loading the solution (e.g., `Debug`, `Release`).
*   `--disable-git`: Disables all Git integration features.

### Stdio Server

The Stdio server communicates over standard input/output.

Configure it in your MCP client of choice.
VSCode Copilot example:

```json
"mcp": {
    "servers": {
        "SharpTools": {
            "type": "stdio",
            "command": "/path/to/repo/SharpToolsMCP/SharpTools.StdioServer/bin/Debug/net8.0/SharpTools.StdioServer",
            "args": [
                "--log-directory",
                "/var/log/sharptools/",
                "--log-level",
                "Debug",
            ]
        }
    }
},
```
Key Options:
*   `--log-directory <path>`: Directory to store log files.
*   `--log-level <level>`: Minimum log level.
*   `--load-solution <path>`: Path to a `.sln` file to load on startup. Useful for manual testing. It is recommended to let the AI run the LoadSolution tool instead, as it returns some useful information.
*   `--build-configuration <config>`: Build configuration to use when loading the solution (e.g., `Debug`, `Release`).
*   `--disable-git`: Disables all Git integration features.

## Contributing

Contributions are welcome! Please feel free to submit pull requests or open issues.

## License

This project is licensed under the MIT License - see the LICENSE file for details.