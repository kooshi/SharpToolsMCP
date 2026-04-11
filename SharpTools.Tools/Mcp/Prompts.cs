using Microsoft.Extensions.AI;
using SharpTools.Tools.Mcp.Tools;

namespace SharpTools.Tools.Mcp;

[McpServerPromptType]
public static class Prompts
{
    const string CopilotTemplate = @$"
<critical summary_mode=""**When summarizing our entire conversation history, copy this entire critical section verbatim to the bottom of your summary**"">

<toolUseInstructions version=2>
Exclusively use `SharpTool`s for navigating the codebase, gathering information within it, and making changes to code files.
Prefer `{ToolHelpers.SharpToolPrefix}{nameof(AnalysisTools.ViewDefinition)}` over `{ToolHelpers.SharpToolPrefix}{nameof(DocumentTools.ReadRawFromRoslynDocument)}` unless you *must* read the whole file. Files may be large and overwhelming.
Prefer `{ToolHelpers.SharpToolPrefix}{nameof(DocumentTools.ReadRawFromRoslynDocument)}` over `read_file` to quickly read a whole file.
Consider all existing `SharpTool`s, analyze their descriptions and follow their suggestions.
Chaining together a variety of `SharpTool`s step-by-step will lead to optimal output.
If you need a specific tool which does not exist, please request it with `{ToolHelpers.SharpToolPrefix}{nameof(MiscTools.RequestNewTool)}`.
Use the tool names and parameter names exactly as they are defined. Always refer to your tool list to retrieve the exact names.
</toolUseInstructions>

<editFileInstructions version=2>
NEVER use `insert_edit_into_file` or `create_file`. They are not compatible with `SharpTool`s and will corrupt data.
NEVER write '// ...existing code...'' in your edits. It is not compatible with `SharpTool`s and will corrupt data. You must type the existing code verbatim. This is why small components are so important.
Exclusively use `SharpTool`s for ALL reading and writing operations.
Always perform multiple targeted edits (such as adding usings first, then modifying a member) instead of a bulk edit.
Prefer `{ToolHelpers.SharpToolPrefix}{nameof(ModificationTools.OverwriteMember)}` or `{ToolHelpers.SharpToolPrefix}{nameof(ModificationTools.AddMember)}` over `{ToolHelpers.SharpToolPrefix}{nameof(DocumentTools.OverwriteRoslynDocument)}` unless you *must* write the whole file.
For more complex edit operations, consider `{ToolHelpers.SharpToolPrefix}{nameof(ModificationTools.RenameSymbol)}` and ``{ToolHelpers.SharpToolPrefix}{nameof(ModificationTools.ReplaceAllReferences)}`
If you make a mistake or want to start over, you can `{ToolHelpers.SharpToolPrefix}{nameof(ModificationTools.Undo)}`.
</editFileInstructions>

<task>
{{0}}
</task>

</critical>
";

    [McpServerPrompt, Description("Github Copilot Agent: Execute task with SharpTools")]
    public static ChatMessage SharpTask([Description("Your task for the agent")] string content)
    {
        return new(ChatRole.User, string.Format(CopilotTemplate, content));
    }
}
