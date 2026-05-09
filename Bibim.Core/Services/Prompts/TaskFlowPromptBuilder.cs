// Copyright (c) 2026 SquareZero Inc. - Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// Shared task-flow prompt builder used by the Revit UI flow and benchmark
    /// runners. Keeping this here prevents the benchmark harness from drifting
    /// away from the real conversation -> task -> codegen path.
    /// </summary>
    public static class TaskFlowPromptBuilder
    {
        public static string BuildPlannerPrompt(bool isEnglish)
        {
            string outputLanguage = isEnglish ? "English" : "Korean";
            string categoryChecklist = CategoryQuestionTemplates.BuildPlannerChecklist();
            return $@"You are the BIBIM task planner for a Revit add-in.
Return JSON only. No markdown. No code fences. No explanations.

Decide whether the latest user message is:
- a direct chat/information request that should be answered immediately (`mode = ""chat""`)
- or an actionable request that should become a task (`mode = ""task""`)

Rules:
1. Greetings, thanks, capability questions, API explanation questions, and troubleshooting explanations are `chat`.
2. Any request to inspect, count, analyze, list, generate, modify, place, rename, delete, export, or execute something in Revit is `task`.
3. For actionable requests, never guess missing details. If anything important is missing, fill `questions` with concrete clarifying questions and set `shouldAutoRun = false`.
4. If there is an active task:
   - use `taskRelation = ""update""` ONLY when the user is clearly refining or answering questions about that SAME task
   - use `taskRelation = ""new""` when the message is a different task or a new request, even if the topic is related
   - use `taskRelation = ""ask""` when it is ambiguous. In that case, put a short user-facing clarification in `assistantMessage`.
   - IMPORTANT: If the user's message describes a completely different action (e.g. active task is ""add parameter"" but user says ""move family""), ALWAYS use `taskRelation = ""new""`.
5. `taskKind = ""read""` means read-only / query / analysis task (e.g. count, list, inspect). If the request is complete, set `shouldAutoRun = true`.
6. `taskKind = ""write""` means any change to the model or generated artifact. If the request is complete, set `shouldAutoRun = false`.
   IMPORTANT: Export operations (PDF, DWG, DXF, CSV, IFC, Excel, image, schedule, etc.) are ALWAYS `taskKind = ""write""` and `shouldAutoRun = false`, even though they do not modify the Revit model. They create file artifacts on disk and must go through the preview -> confirm -> execute flow.
7. Write all user-facing strings (`title`, `summary`, `questions`, `assistantMessage`) in {outputLanguage}.
8. Keep `steps` high-level, natural-language, and free of API syntax.
9. If the user asks about a recent execution failure (e.g. ""왜 실패했어?"", ""에러 원인"", ""why did it fail""), check the [RECENT EXECUTION LOG] section and answer as `mode = ""chat""` with the failure details in `assistantMessage`.
10. Delegation expressions like ""알아서해"", ""마음대로"", ""아무거나"", ""상관없어"", ""니가 결정해"", ""default로"" mean the user wants you to choose sensible defaults. Treat any pending clarification as answered and proceed.

JSON schema:
{{
  ""mode"": ""chat"" | ""task"",
  ""taskKind"": ""read"" | ""write"",
  ""taskRelation"": ""new"" | ""update"" | ""ask"",
  ""title"": ""short task title"",
  ""summary"": ""one paragraph summary"",
  ""steps"": [""step 1"", ""step 2""],
  ""questions"": [
    {{
      ""text"": ""question text"",
      ""selectionType"": ""single"" | ""multi"",
      ""options"": [""option 1"", ""option 2"", ""option 3""]
    }}
  ],
  ""assistantMessage"": ""direct answer for chat mode OR short clarification for taskRelation=ask"",
  ""shouldAutoRun"": true | false
}}

Question rules:
- Each question MUST have 2-5 concrete options the user can click.
- Use ""single"" when only one answer makes sense, ""multi"" when multiple can apply.
- Options should be short, clear labels (not full sentences).
- The user can also type a custom answer, so options don't need to cover every case.

{categoryChecklist}";
        }

        public static string BuildPlannerInput(
            string userText,
            string resolvedText,
            TaskState activeTask,
            bool activeTaskIsTerminal,
            IEnumerable<ChatMessage> recentHistory,
            string executionLog)
        {
            string[] historyLines = (recentHistory ?? Enumerable.Empty<ChatMessage>())
                .Select(m =>
                {
                    string text = m.Text ?? string.Empty;
                    if (text.Length > BibimConstants.PlannerHistoryTurnMaxChars)
                        text = text.Substring(0, BibimConstants.PlannerHistoryTurnMaxChars) + "...";
                    return $"{(m.IsUser ? "USER" : "ASSISTANT")}: {text}";
                })
                .ToArray();

            string currentTask = activeTask == null || activeTaskIsTerminal
                ? "None"
                : JsonHelper.Serialize(new
                {
                    activeTask.TaskId,
                    activeTask.Title,
                    activeTask.Summary,
                    activeTask.Kind,
                    activeTask.Stage,
                    activeTask.Steps,
                    activeTask.Questions,
                    activeTask.RequiresApply
                }, indented: true);

            string contextBlock = resolvedText != userText
                ? resolvedText
                : "(none)";

            if (string.IsNullOrWhiteSpace(executionLog))
                executionLog = "(no recent executions)";

            return $@"[LATEST USER MESSAGE]
{userText}

[RESOLVED REVIT CONTEXT]
{contextBlock}

[ACTIVE TASK]
{currentTask}

[RECENT EXECUTION LOG]
{executionLog}

[RECENT CONVERSATION]
{string.Join(Environment.NewLine, historyLines)}

[Output format: respond with JSON only - no markdown, no commentary.]";
        }

        public static string BuildTaskExecutionPrompt(
            TaskState task,
            string executionLog,
            bool isEnglish)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));

            if (string.IsNullOrWhiteSpace(executionLog))
                executionLog = "(no recent executions)";

            string commentLangRule = isEnglish
                ? "\n- Write all C# code comments and user-visible string literals in English."
                : "\n- 코드 주석과 사용자에게 표시되는 문자열은 모두 한국어로 작성하세요.";

            return $@"Implement the following Revit task.

Task title:
{task.Title}

Task summary:
{task.Summary}

Task kind:
{task.Kind}

Required behavior:
{string.Join(Environment.NewLine, (task.Steps ?? new List<string>()).Select((step, index) => $"{index + 1}. {step}"))}

User-provided details:
{string.Join(Environment.NewLine, (task.CollectedInputs ?? new List<string>()).Select(input => $"- {input}"))}

Recent execution history (use this to avoid repeating the same errors):
{executionLog}

Constraints:
- Respect the current Revit version and available API surface.
- Do not guess requirements beyond the task summary.
- Prefer safe, minimal changes.
- If a previous execution failed, analyze the error and generate code that avoids the same failure.
- Return ONLY a ```csharp``` block containing statements for the body of Execute(UIApplication uiApp, Bibim.Core.BibimExecutionContext ctx).
- Use ctx.Log(""message"") to record intermediate progress (e.g. element counts, decisions, skipped items). These appear in the BIBIM panel after execution.
- Do NOT include using directives, namespace, class, method signature, markdown outside the code block, or explanation text.{commentLangRule}";
        }

        public static TaskPlanResponse TryParsePlan(string rawText, out string errorMessage)
        {
            errorMessage = null;
            string json = ExtractJsonObject(rawText);
            if (string.IsNullOrWhiteSpace(json))
            {
                errorMessage = "Task planner returned non-JSON content.";
                return null;
            }

            TaskPlanResponse plan;
            try
            {
                plan = JsonHelper.Deserialize<TaskPlanResponse>(json);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }

            if (plan == null || string.IsNullOrWhiteSpace(plan.Mode))
            {
                errorMessage = "Task planner returned an invalid payload.";
                return null;
            }

            try
            {
                var jObj = JObject.Parse(json);
                var questionsToken = jObj["questions"];
                if (questionsToken is JArray questionsArray && questionsArray.Count > 0)
                {
                    var normalized = new List<QuestionItem>();
                    foreach (var item in questionsArray)
                    {
                        if (item.Type == JTokenType.String)
                        {
                            normalized.Add(new QuestionItem
                            {
                                Text = item.ToString(),
                                SelectionType = "single",
                                Options = new List<string>()
                            });
                        }
                        else if (item.Type == JTokenType.Object)
                        {
                            var qi = item.ToObject<QuestionItem>();
                            if (qi != null && !string.IsNullOrWhiteSpace(qi.Text))
                            {
                                if (qi.Options == null) qi.Options = new List<string>();
                                if (string.IsNullOrWhiteSpace(qi.SelectionType)) qi.SelectionType = "single";
                                normalized.Add(qi);
                            }
                        }
                    }
                    plan.Questions = normalized;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("TaskPlanner", $"Questions normalization fallback: {ex.Message}");
            }

            return plan;
        }

        public static string ExtractJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            text = Regex.Replace(text, @"^\s*```(?:json)?\s*\n?", "", RegexOptions.Multiline);
            text = Regex.Replace(text, @"\n?\s*```\s*$", "", RegexOptions.Multiline);
            text = text.Trim();

            var match = Regex.Match(text, "\\{[\\s\\S]*\\}");
            return match.Success ? match.Value : null;
        }
    }
}
