// Copyright (c) 2026 SquareZero Inc. - Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bibim.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bibim.OpenRouterBench
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var options = BenchOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }
            AppLanguage.Initialize(options.Language);

            string repoRoot = FindRepoRoot();
            string key = LoadApiKey(options, repoRoot);
            if (string.IsNullOrWhiteSpace(key))
            {
                Console.Error.WriteLine("OpenRouter key missing. Use --key-path, OPENROUTER_API_KEY, or ../key.txt.");
                return 2;
            }

            string revitPath = ResolveRevitPath(options.RevitPath, options.RevitVersion);
            Environment.SetEnvironmentVariable("REVIT_SDK_PATH", revitPath);
            LoadRevitAssemblies(revitPath);

            string modelPath = FullPath(repoRoot, options.ModelsPath);
            string promptPath = FullPath(repoRoot, options.PromptsPath);
            var models = LoadJson<List<BenchModel>>(modelPath)
                .Where(m => m.Enabled && !m.NotOnOpenRouter)
                .ToList();
            var prompts = LoadJson<List<BenchPrompt>>(promptPath).ToList();

            if (!string.IsNullOrWhiteSpace(options.ModelFilter))
                models = models.Where(m => string.Equals(m.Id, options.ModelFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(options.PromptFilter))
                prompts = prompts.Where(p => string.Equals(p.Id, options.PromptFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (options.MaxModels > 0) models = models.Take(options.MaxModels).ToList();
            if (options.MaxPrompts > 0) prompts = prompts.Take(options.MaxPrompts).ToList();

            if (models.Count == 0 || prompts.Count == 0)
            {
                Console.Error.WriteLine($"No benchmark work selected. models={models.Count}, prompts={prompts.Count}");
                return 3;
            }

            string outputRoot = ResolveOutputRoot(options.OutputRoot);
            Directory.CreateDirectory(outputRoot);
            Directory.CreateDirectory(Path.Combine(outputRoot, "runs"));

            string summaryCsv = Path.Combine(outputRoot, "summary.csv");
            string resultsJsonl = Path.Combine(outputRoot, "results.jsonl");
            File.WriteAllText(summaryCsv, BenchResult.CsvHeader + Environment.NewLine);

            Console.WriteLine($"OpenRouter benchmark output: {outputRoot}");
            Console.WriteLine($"Models: {models.Count}, prompts: {prompts.Count}, Revit path: {revitPath}");

            foreach (var model in models)
            {
                foreach (var prompt in prompts)
                {
                    var result = await RunOneAsync(model, prompt, key, options, outputRoot);
                    File.AppendAllText(summaryCsv, result.ToCsvRow() + Environment.NewLine);
                    File.AppendAllText(resultsJsonl, JsonConvert.SerializeObject(result) + Environment.NewLine);

                    string status = result.CompileSuccess ? "COMPILE_OK" : "COMPILE_FAIL";
                    Console.WriteLine($"{status} flow={result.FlowMode} model={model.Id} prompt={prompt.Id} total={result.ElapsedMs}ms planner={result.PlannerElapsedMs}ms codegen={result.CodegenElapsedMs}ms turns={result.LlmTurnCount} tools={result.ToolCallCount}");
                }
            }

            Console.WriteLine($"Summary: {summaryCsv}");
            Console.WriteLine($"JSONL:   {resultsJsonl}");
            return 0;
        }

        private static async Task<BenchResult> RunOneAsync(
            BenchModel model,
            BenchPrompt prompt,
            string key,
            BenchOptions options,
            string outputRoot)
        {
            string safeRunId = Sanitize($"{model.Id}_{prompt.Id}_{DateTime.Now:HHmmssfff}", 90);
            string debugDir = Path.Combine(outputRoot, "runs", safeRunId);
            Directory.CreateDirectory(debugDir);

            var compiler = new RoslynCompilerService();
            var analyzer = new RoslynAnalyzerService();
            string revitVersion = options.RevitVersion;
            analyzer.SetRevitVersion(revitVersion);
            var llm = new LlmOrchestrationService(model.Id, key, compiler);

            if (string.Equals(options.Flow, "app", StringComparison.OrdinalIgnoreCase))
            {
                return await RunAppFlowAsync(
                    model, prompt, key, options, debugDir, compiler, analyzer, llm);
            }

            var forcedRag = await BuildForcedRagContextAsync(prompt, revitVersion, debugDir, CancellationToken.None);
            string systemPrompt = CodeGenSystemPrompt.Build(revitVersion, isCodeGeneration: true, isFileOutput: false)
                + CodeGenSystemPrompt.AppendRagContext(forcedRag.Context)
                + CodeGenSystemPrompt.AppendToolUseInstructions()
                + "\n\nBENCHMARK MODE:\n"
                + "- Revit live context tools are not available in this compile benchmark.\n"
                + "- Use uidoc/doc/app/ctx variables that BIBIM injects at runtime.\n"
                + "- Return only a single ```csharp``` block for the final answer.";

            string taskPrompt = "[Benchmark prompt]\n" + prompt.Prompt + "\n\nConstraints:\n"
                + "- Return ONLY a ```csharp``` block containing statements for the body of Execute(UIApplication uiApp, Bibim.Core.BibimExecutionContext ctx).\n"
                + "- Do NOT include using directives, namespace, class, method signature, or explanation text.\n"
                + "- Do NOT redeclare app, doc, uidoc, or ctx; they already exist in scope.\n";
            var history = new List<ChatMessage>
            {
                new ChatMessage { IsUser = true, Text = taskPrompt }
            };

            CodegenDebugRecorder.WriteJson(debugDir, "model.json", model);
            CodegenDebugRecorder.WriteJson(debugDir, "prompt.json", prompt);
            CodegenDebugRecorder.WriteText(debugDir, "system_prompt.txt", systemPrompt);
            CodegenDebugRecorder.WriteText(debugDir, "task_prompt.txt", taskPrompt);

            var sw = Stopwatch.StartNew();
            CodeGenerationResult codeResult;
            try
            {
                codeResult = await llm.GenerateWithToolsAsync(
                    history,
                    systemPrompt,
                    BuildBenchmarkToolDefinitions(),
                    (toolName, inputJson, ct) => ExecuteBenchmarkToolAsync(toolName, inputJson, compiler, analyzer, revitVersion, ct),
                    maxTurns: 12,
                    debugDirectory: debugDir,
                    ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                sw.Stop();
                var failed = BenchResult.FromException(model, prompt, debugDir, sw.ElapsedMilliseconds, ex);
                ApplyBenchmarkProcessMetrics(failed, forcedRag, clarificationRounds: 0);
                return failed;
            }
            sw.Stop();

            if (codeResult.CompilationResult != null)
                CodegenDebugRecorder.WriteCompilationArtifacts(debugDir, "final_compile", codeResult.CompilationResult);
            if (!string.IsNullOrWhiteSpace(codeResult.GeneratedCode))
                CodegenDebugRecorder.WriteText(debugDir, "generated_code.cs", codeResult.GeneratedCode);
            CodegenDebugRecorder.WriteJson(debugDir, "llm_turn_metrics.json", codeResult.TurnMetrics);

            var benchResult = BenchResult.FromCodegen(model, prompt, debugDir, sw.ElapsedMilliseconds, codeResult);
            ApplyBenchmarkProcessMetrics(benchResult, forcedRag, clarificationRounds: 0);
            benchResult.FlowMode = "codegen";
            benchResult.CodegenElapsedMs = sw.ElapsedMilliseconds;
            CodegenDebugRecorder.WriteJson(debugDir, "benchmark_result.json", benchResult);
            return benchResult;
        }

        private static async Task<BenchResult> RunAppFlowAsync(
            BenchModel model,
            BenchPrompt prompt,
            string key,
            BenchOptions options,
            string debugDir,
            RoslynCompilerService compiler,
            RoslynAnalyzerService analyzer,
            LlmOrchestrationService llm)
        {
            string revitVersion = options.RevitVersion;
            string userMessage = string.IsNullOrWhiteSpace(prompt.UserMessage)
                ? prompt.Prompt
                : prompt.UserMessage;
            int clarificationRounds = 0;
            ForcedRagBundle forcedRag = null;

            CodegenDebugRecorder.WriteJson(debugDir, "model.json", model);
            CodegenDebugRecorder.WriteJson(debugDir, "prompt.json", prompt);
            CodegenDebugRecorder.WriteText(debugDir, "user_message.txt", userMessage);

            var totalSw = Stopwatch.StartNew();
            var plannerMetrics = new List<PlannerResponseMetric>();
            TaskPlanResponse plan;
            LlmResponse plannerResponse;

            string plannerSystemPrompt = TaskFlowPromptBuilder.BuildPlannerPrompt(AppLanguage.IsEnglish);
            string plannerInput = TaskFlowPromptBuilder.BuildPlannerInput(
                userMessage,
                userMessage,
                activeTask: null,
                activeTaskIsTerminal: true,
                recentHistory: new List<ChatMessage>(),
                executionLog: "(no recent executions)");
            var planningHistory = new List<ChatMessage>
            {
                new ChatMessage { IsUser = true, Text = plannerInput }
            };

            CodegenDebugRecorder.WriteText(debugDir, "planner_system_prompt.txt", plannerSystemPrompt);
            CodegenDebugRecorder.WriteText(debugDir, "planner_input.txt", plannerInput);

            try
            {
                plannerResponse = await llm.SendMessageNonStreamingAsync(
                    planningHistory,
                    plannerSystemPrompt,
                    maxTokens: 1024,
                    ct: CancellationToken.None,
                    jsonMode: true);
            }
            catch (Exception ex)
            {
                totalSw.Stop();
                var failed = BenchResult.FromException(model, prompt, debugDir, totalSw.ElapsedMilliseconds, ex);
                failed.FlowMode = "app";
                return failed;
            }

            plannerMetrics.Add(PlannerResponseMetric.FromResponse(1, plannerResponse));
            CodegenDebugRecorder.WriteText(debugDir, "planner_response_01.txt", plannerResponse.Text);

            if (!plannerResponse.Success || string.IsNullOrWhiteSpace(plannerResponse.Text))
            {
                totalSw.Stop();
                var failed = BenchResult.FromException(
                    model,
                    prompt,
                    debugDir,
                    totalSw.ElapsedMilliseconds,
                    new InvalidOperationException(plannerResponse.ErrorMessage ?? "Task planner failed."));
                failed.FlowMode = "app";
                failed.PlannerElapsedMs = plannerResponse.ElapsedMs;
                failed.PlannerResponseCount = plannerMetrics.Count;
                CodegenDebugRecorder.WriteJson(debugDir, "planner_response_metrics.json", plannerMetrics);
                CodegenDebugRecorder.WriteJson(debugDir, "benchmark_result.json", failed);
                return failed;
            }

            plan = TaskFlowPromptBuilder.TryParsePlan(plannerResponse.Text, out string parseError);
            if (plan == null)
            {
                var retryHistory = new List<ChatMessage>(planningHistory)
                {
                    new ChatMessage { Text = plannerResponse.Text, IsUser = false },
                    new ChatMessage
                    {
                        Text = "Your previous response was not valid JSON (parse error: " +
                               parseError + "). " +
                               "Return ONLY a single complete JSON object that matches the schema. " +
                               "Do NOT use markdown. Do NOT use code fences. " +
                               "Do NOT add any commentary before or after the JSON.",
                        IsUser = true
                    }
                };

                var retryResponse = await llm.SendMessageNonStreamingAsync(
                    retryHistory,
                    plannerSystemPrompt,
                    maxTokens: 1024,
                    ct: CancellationToken.None,
                    jsonMode: true);
                plannerMetrics.Add(PlannerResponseMetric.FromResponse(2, retryResponse));
                CodegenDebugRecorder.WriteText(debugDir, "planner_response_02_retry.txt", retryResponse.Text);

                plan = TaskFlowPromptBuilder.TryParsePlan(retryResponse.Text, out string retryError);
                if (plan == null)
                {
                    totalSw.Stop();
                    var failed = BenchResult.FromException(
                        model,
                        prompt,
                        debugDir,
                        totalSw.ElapsedMilliseconds,
                        new InvalidOperationException(retryError ?? parseError));
                    failed.FlowMode = "app";
                    failed.PlannerElapsedMs = plannerMetrics.Sum(m => m.ElapsedMs);
                    failed.PlannerResponseCount = plannerMetrics.Count;
                    CodegenDebugRecorder.WriteJson(debugDir, "planner_response_metrics.json", plannerMetrics);
                    CodegenDebugRecorder.WriteJson(debugDir, "benchmark_result.json", failed);
                    return failed;
                }
            }

            CodegenDebugRecorder.WriteJson(debugDir, "planner_response_metrics.json", plannerMetrics);
            CodegenDebugRecorder.WriteJson(debugDir, "task_plan.json", plan);

            if (string.Equals(plan.Mode, "task", StringComparison.OrdinalIgnoreCase) &&
                plan.Questions != null &&
                plan.Questions.Count > 0)
            {
                clarificationRounds++;
                string clarificationAnswer = BuildBenchmarkClarificationAnswer(prompt, plan);
                CodegenDebugRecorder.WriteJson(debugDir, "planner_questions_01.json", plan.Questions);
                CodegenDebugRecorder.WriteText(debugDir, "benchmark_clarification_answer_01.txt", clarificationAnswer);

                var activeTaskForClarification = CreateTaskFromPlan(plan, userMessage);
                activeTaskForClarification.Stage = TaskStages.NeedsDetails;

                var clarificationHistory = new List<ChatMessage>
                {
                    new ChatMessage { IsUser = true, Text = userMessage },
                    new ChatMessage { IsUser = false, Text = BuildQuestionTranscript(plan) },
                    new ChatMessage { IsUser = true, Text = clarificationAnswer }
                };

                string clarificationPlannerInput = TaskFlowPromptBuilder.BuildPlannerInput(
                    clarificationAnswer,
                    clarificationAnswer,
                    activeTaskForClarification,
                    activeTaskIsTerminal: false,
                    recentHistory: clarificationHistory,
                    executionLog: "(no recent executions)");

                CodegenDebugRecorder.WriteText(debugDir, "planner_input_02_clarification.txt", clarificationPlannerInput);

                var clarificationPlannerResponse = await llm.SendMessageNonStreamingAsync(
                    new List<ChatMessage> { new ChatMessage { IsUser = true, Text = clarificationPlannerInput } },
                    plannerSystemPrompt,
                    maxTokens: 1024,
                    ct: CancellationToken.None,
                    jsonMode: true);

                plannerMetrics.Add(PlannerResponseMetric.FromResponse(plannerMetrics.Count + 1, clarificationPlannerResponse));
                CodegenDebugRecorder.WriteText(debugDir, "planner_response_03_clarification.txt", clarificationPlannerResponse.Text);

                TaskPlanResponse clarifiedPlan = null;
                if (clarificationPlannerResponse.Success && !string.IsNullOrWhiteSpace(clarificationPlannerResponse.Text))
                    clarifiedPlan = TaskFlowPromptBuilder.TryParsePlan(clarificationPlannerResponse.Text, out _);

                if (clarifiedPlan != null &&
                    string.Equals(clarifiedPlan.Mode, "task", StringComparison.OrdinalIgnoreCase))
                {
                    plan = clarifiedPlan;
                    CodegenDebugRecorder.WriteJson(debugDir, "task_plan_after_clarification.json", plan);
                }
                else
                {
                    CodegenDebugRecorder.WriteText(
                        debugDir,
                        "planner_clarification_fallback.txt",
                        "The clarification planner response was not a task plan. Continuing with the first task plan plus the benchmark clarification answer.");
                }
            }

            if (!string.Equals(plan.Mode, "task", StringComparison.OrdinalIgnoreCase))
            {
                totalSw.Stop();
                var chatResult = BenchResult.FromPlannerOnly(model, prompt, debugDir, totalSw.ElapsedMilliseconds, plan, plannerMetrics);
                ApplyBenchmarkProcessMetrics(chatResult, forcedRag, clarificationRounds);
                chatResult.FlowMode = "app";
                chatResult.ErrorMessage = "Planner routed the prompt to chat mode.";
                CodegenDebugRecorder.WriteJson(debugDir, "benchmark_result.json", chatResult);
                return chatResult;
            }

            var task = CreateTaskFromPlan(plan, userMessage);
            task.Stage = TaskStages.Working;
            if (clarificationRounds > 0)
                task.CollectedInputs.Add(BuildBenchmarkClarificationAnswer(prompt, plan));
            CodegenDebugRecorder.WriteJson(debugDir, "task_snapshot.json", task);

            string taskPrompt = TaskFlowPromptBuilder.BuildTaskExecutionPrompt(
                task,
                "(no recent executions)",
                AppLanguage.IsEnglish);

            bool isFileOutput =
                CodeGenSystemPrompt.LooksLikeFileOutputTask(task.Title) ||
                CodeGenSystemPrompt.LooksLikeFileOutputTask(task.Summary) ||
                CodeGenSystemPrompt.LooksLikeFileOutputTask(task.SourceUserMessage) ||
                CodeGenSystemPrompt.LooksLikeFileOutputTask(taskPrompt);

            string systemPrompt = CodeGenSystemPrompt.Build(revitVersion, true, isFileOutput)
                + CodeGenSystemPrompt.AppendRagContext((forcedRag = await BuildForcedRagContextAsync(prompt, revitVersion, debugDir, CancellationToken.None)).Context)
                + CodeGenSystemPrompt.AppendToolUseInstructions();

            CodegenDebugRecorder.WriteText(debugDir, "system_prompt.txt", systemPrompt);
            CodegenDebugRecorder.WriteText(debugDir, "task_prompt.txt", taskPrompt);

            var generationHistory = new List<ChatMessage>
            {
                new ChatMessage { IsUser = true, Text = userMessage },
                new ChatMessage { IsUser = true, Text = taskPrompt }
            };
            CodegenDebugRecorder.WriteJson(debugDir, "generation_history.json", generationHistory);

            string toolHint = string.Join(" ",
                task.Title ?? "",
                task.Summary ?? "",
                task.SourceUserMessage ?? "",
                taskPrompt ?? "");

            var codegenSw = Stopwatch.StartNew();
            CodeGenerationResult codeResult;
            try
            {
                codeResult = await llm.GenerateWithToolsAsync(
                    generationHistory,
                    systemPrompt,
                    BibimToolService.GetToolDefinitions(toolHint),
                    (toolName, inputJson, ct) => ExecuteBenchmarkToolAsync(toolName, inputJson, compiler, analyzer, revitVersion, ct),
                    maxTurns: 15,
                    debugDirectory: debugDir,
                    ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                codegenSw.Stop();
                totalSw.Stop();
                var failed = BenchResult.FromException(model, prompt, debugDir, totalSw.ElapsedMilliseconds, ex);
                ApplyBenchmarkProcessMetrics(failed, forcedRag, clarificationRounds);
                failed.FlowMode = "app";
                failed.PlannerElapsedMs = plannerMetrics.Sum(m => m.ElapsedMs);
                failed.PlannerResponseCount = plannerMetrics.Count;
                failed.CodegenElapsedMs = codegenSw.ElapsedMilliseconds;
                CodegenDebugRecorder.WriteJson(debugDir, "benchmark_result.json", failed);
                return failed;
            }
            codegenSw.Stop();
            totalSw.Stop();

            if (codeResult.CompilationResult != null)
                CodegenDebugRecorder.WriteCompilationArtifacts(debugDir, "final_compile", codeResult.CompilationResult);
            if (!string.IsNullOrWhiteSpace(codeResult.GeneratedCode))
                CodegenDebugRecorder.WriteText(debugDir, "generated_code.cs", codeResult.GeneratedCode);
            CodegenDebugRecorder.WriteJson(debugDir, "llm_turn_metrics.json", codeResult.TurnMetrics);

            var benchResult = BenchResult.FromCodegen(model, prompt, debugDir, totalSw.ElapsedMilliseconds, codeResult);
            ApplyBenchmarkProcessMetrics(benchResult, forcedRag, clarificationRounds);
            benchResult.FlowMode = "app";
            benchResult.PlannerMode = plan.Mode;
            benchResult.PlannerTaskKind = plan.TaskKind;
            benchResult.PlannerShouldAutoRun = plan.ShouldAutoRun;
            benchResult.PlannerQuestionCount = plan.Questions?.Count ?? 0;
            benchResult.PlannerElapsedMs = plannerMetrics.Sum(m => m.ElapsedMs);
            benchResult.PlannerResponseCount = plannerMetrics.Count;
            benchResult.CodegenElapsedMs = codegenSw.ElapsedMilliseconds;
            CodegenDebugRecorder.WriteJson(debugDir, "benchmark_result.json", benchResult);
            return benchResult;
        }

        private static async Task<ForcedRagBundle> BuildForcedRagContextAsync(
            BenchPrompt prompt,
            string revitVersion,
            string debugDir,
            CancellationToken ct)
        {
            var bundle = new ForcedRagBundle();
            var queries = (prompt.RagQueries ?? new List<string>())
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (queries.Count == 0)
                return bundle;

            var sb = new StringBuilder();
            var records = new List<object>();

            foreach (string query in queries)
            {
                var sw = Stopwatch.StartNew();
                string resultText = await SearchRevitApiAsync(
                    new JObject { ["query"] = query },
                    revitVersion,
                    ct);
                sw.Stop();

                bundle.CallCount++;
                bundle.ElapsedMs += sw.ElapsedMilliseconds;
                sb.AppendLine($"[Forced RAG query: {query}]");
                sb.AppendLine(resultText);
                sb.AppendLine();

                records.Add(new
                {
                    query,
                    elapsedMs = sw.ElapsedMilliseconds,
                    resultChars = resultText?.Length ?? 0,
                    preview = Shorten(resultText, 500)
                });
            }

            bundle.Context = sb.ToString().Trim();
            CodegenDebugRecorder.WriteText(debugDir, "forced_rag_context.txt", bundle.Context);
            CodegenDebugRecorder.WriteJson(debugDir, "forced_rag_records.json", records);
            return bundle;
        }

        private static string BuildBenchmarkClarificationAnswer(BenchPrompt prompt, TaskPlanResponse plan)
        {
            if (!string.IsNullOrWhiteSpace(prompt.ClarificationAnswer))
                return prompt.ClarificationAnswer;

            var defaults = new StringBuilder();
            defaults.Append("Use benchmark defaults and proceed. ");
            defaults.Append("The current Revit document is already the opened target document. ");
            defaults.Append("Do not open, search for, or load a separate RFA/RVT file. ");
            defaults.Append("Use the injected doc, uidoc, app, uiApp, and ctx variables only. ");
            defaults.Append("If this is a family document task, use family-safe APIs such as doc.FamilyCreate and doc.FamilyManager. ");

            if (plan?.Questions != null && plan.Questions.Count > 0)
            {
                defaults.Append("For every clarification question, choose the safest visible benchmark default. ");
                foreach (var question in plan.Questions)
                {
                    string option = question.Options?.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o));
                    if (!string.IsNullOrWhiteSpace(option))
                        defaults.Append($"Question: {question.Text} Answer: {option}. ");
                }
            }

            return defaults.ToString();
        }

        private static string BuildQuestionTranscript(TaskPlanResponse plan)
        {
            if (plan?.Questions == null || plan.Questions.Count == 0)
                return "No clarification questions.";

            var lines = new List<string> { "Clarification questions:" };
            foreach (var question in plan.Questions)
            {
                string options = question.Options == null || question.Options.Count == 0
                    ? ""
                    : " Options: " + string.Join(", ", question.Options);
                lines.Add($"- {question.Text}{options}");
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static void ApplyBenchmarkProcessMetrics(
            BenchResult result,
            ForcedRagBundle forcedRag,
            int clarificationRounds)
        {
            if (result == null) return;
            result.ForcedRagCallCount = forcedRag?.CallCount ?? 0;
            result.ForcedRagElapsedMs = forcedRag?.ElapsedMs ?? 0;
            result.ClarificationRounds = clarificationRounds;
            result.RagCallCount += result.ForcedRagCallCount;
        }

        private static string Shorten(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
                return text ?? "";
            return text.Substring(0, maxChars) + "...";
        }

        private static TaskState CreateTaskFromPlan(TaskPlanResponse plan, string userText)
        {
            return new TaskState
            {
                TaskId = Guid.NewGuid().ToString(),
                Title = string.IsNullOrWhiteSpace(plan.Title) ? "Benchmark task" : plan.Title,
                Summary = plan.Summary ?? "",
                Kind = string.Equals(plan.TaskKind, TaskKinds.Read, StringComparison.OrdinalIgnoreCase)
                    ? TaskKinds.Read
                    : TaskKinds.Write,
                RequiresApply = !string.Equals(plan.TaskKind, TaskKinds.Read, StringComparison.OrdinalIgnoreCase),
                Stage = TaskStages.NeedsDetails,
                Steps = plan.Steps?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>(),
                Questions = plan.Questions?.Where(q => !string.IsNullOrWhiteSpace(q.Text)).ToList() ?? new List<QuestionItem>(),
                SourceUserMessage = userText,
                CollectedInputs = new List<string> { userText },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        private static T LoadJson<T>(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Required benchmark file not found: {path}");
            return JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
        }

        private static JArray BuildBenchmarkToolDefinitions()
        {
            return new JArray
            {
                new JObject
                {
                    ["name"] = "search_revit_api",
                    ["description"] = "Search the local Revit API documentation index built from RevitAPI.xml. Use this to verify class names, method signatures, constructor parameters, and property names.",
                    ["input_schema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["query"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "English search query, e.g. PDFExportOptions"
                            }
                        },
                        ["required"] = new JArray { "query" }
                    }
                },
                new JObject
                {
                    ["name"] = "run_roslyn_check",
                    ["description"] = "Compile C# code with Roslyn and run BIBIM static analyzers. Use this before returning final code.",
                    ["input_schema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["code"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "The C# code to compile and analyze"
                            }
                        },
                        ["required"] = new JArray { "code" }
                    }
                }
            };
        }

        private static async Task<string> ExecuteBenchmarkToolAsync(
            string toolName,
            string inputJson,
            RoslynCompilerService compiler,
            RoslynAnalyzerService analyzer,
            string revitVersion,
            CancellationToken ct)
        {
            var input = string.IsNullOrWhiteSpace(inputJson)
                ? new JObject()
                : JObject.Parse(inputJson);

            switch (toolName)
            {
                case "search_revit_api":
                    return await SearchRevitApiAsync(input, revitVersion, ct);
                case "run_roslyn_check":
                    return RunRoslynCheck(input, compiler, analyzer);
                case "get_view_info":
                case "get_selected_elements":
                case "get_element_parameters":
                case "get_family_types":
                case "get_project_levels":
                    return "[Tool Error] Live Revit context tools are not available in the console benchmark. Generate code that uses uiApp, uidoc, doc, and ctx at runtime.";
                default:
                    return $"[Tool Error] Unknown benchmark tool: {toolName}";
            }
        }

        private static async Task<string> SearchRevitApiAsync(JObject input, string revitVersion, CancellationToken ct)
        {
            string query = input["query"]?.ToString();
            if (string.IsNullOrWhiteSpace(query))
                return "[Tool Error] 'query' parameter is required.";

            Type ragType = typeof(CodeGenSystemPrompt).Assembly.GetType("Bibim.Core.LocalRevitRagService");
            if (ragType == null)
                return "[Tool Error] LocalRevitRagService not found.";

            MethodInfo method = ragType.GetMethod(
                "FetchAsync",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
                return "[Tool Error] LocalRevitRagService.FetchAsync not found.";

            var task = (Task<RagFetchResult>)method.Invoke(null, new object[] { query, revitVersion, ct });
            RagFetchResult result = await task;
            string header = $"[RAG: {result.ElapsedMs}ms, {result.Status}]\n";
            if (result.HasContext)
                return header + result.ContextText;

            return header + $"[No API documentation found for: {query}]"
                + (result.ErrorSummary != null ? $" - {result.ErrorSummary}" : "");
        }

        private static string RunRoslynCheck(
            JObject input,
            RoslynCompilerService compiler,
            RoslynAnalyzerService analyzer)
        {
            string code = input["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code))
                return "[Tool Error] 'code' parameter is required.";

            var sb = new StringBuilder();
            string codeToCompile = code;
            if (analyzer != null)
            {
                var analyzerReport = analyzer.Analyze(code);
                var fixResult = analyzer.ApplyAutoFixes(code);
                if (fixResult.HasChanges)
                {
                    codeToCompile = fixResult.FixedCode;
                    sb.AppendLine($"AUTO-FIXES APPLIED: {string.Join(", ", fixResult.AppliedFixes)}");
                }
                if (fixResult.SuggestedFixes?.Count > 0)
                {
                    sb.AppendLine("SUGGESTED FIXES:");
                    foreach (var fix in fixResult.SuggestedFixes)
                        sb.AppendLine($"  - {fix}");
                }
                if (analyzerReport.HasErrors || analyzerReport.HasWarnings)
                    sb.AppendLine($"ANALYZER: {analyzerReport.FormatSummary()}");
            }

            var compileResult = compiler.Compile(codeToCompile);
            if (compileResult.Success)
            {
                sb.Insert(0, "COMPILE: SUCCESS\n");
                if (!string.Equals(code, codeToCompile, StringComparison.Ordinal))
                {
                    sb.AppendLine("FIXED CODE (use this version):");
                    sb.AppendLine("```csharp");
                    sb.AppendLine(codeToCompile);
                    sb.AppendLine("```");
                }
            }
            else
            {
                sb.Insert(0, "COMPILE: FAILED\n");
                sb.AppendLine("ERRORS:");
                sb.AppendLine(compileResult.ErrorSummary);
            }

            return sb.ToString().TrimEnd();
        }

        private static string LoadApiKey(BenchOptions options, string repoRoot)
        {
            string env = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            string keyPath = options.KeyPath;
            if (string.IsNullOrWhiteSpace(keyPath))
                keyPath = Path.Combine(repoRoot, "..", "key.txt");
            keyPath = FullPath(repoRoot, keyPath);

            if (!File.Exists(keyPath)) return null;
            return File.ReadAllText(keyPath).Trim();
        }

        private static string ResolveRevitPath(string explicitPath, string revitVersion)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return explicitPath;

            string env = Environment.GetEnvironmentVariable("REVIT_SDK_PATH");
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Autodesk",
                $"Revit {revitVersion}");
        }

        private static void LoadRevitAssemblies(string revitPath)
        {
            foreach (string name in new[] { "RevitAPI.dll" })
            {
                string path = Path.Combine(revitPath, name);
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"Warning: {name} not found at {path}");
                    continue;
                }

                try
                {
                    Assembly.LoadFrom(path);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: failed to load {path}: {ex.Message}");
                }
            }
        }

        private static string ResolveOutputRoot(string explicitOutput)
        {
            if (!string.IsNullOrWhiteSpace(explicitOutput))
                return Path.GetFullPath(explicitOutput);

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BIBIM",
                "debug",
                "openrouter-bench",
                DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        }

        private static string FindRepoRoot()
        {
            string dir = Directory.GetCurrentDirectory();
            while (!string.IsNullOrWhiteSpace(dir))
            {
                if (File.Exists(Path.Combine(dir, "Bibim.V3.sln")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
            return Directory.GetCurrentDirectory();
        }

        private static string FullPath(string repoRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(repoRoot, path));
        }

        private static string Sanitize(string value, int maxLength)
        {
            var chars = (value ?? "run")
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
                .ToArray();
            string sanitized = new string(chars).Trim('_');
            if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "run";
            return sanitized.Length > maxLength ? sanitized.Substring(0, maxLength) : sanitized;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  dotnet run --project Bibim.OpenRouterBench -- [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --key-path <path>       OpenRouter key file. Defaults to ../key.txt.");
            Console.WriteLine("  --models <path>         Models JSON. Defaults to benchmarks/openrouter/models.json.");
            Console.WriteLine("  --prompts <path>        Prompts JSON. Defaults to benchmarks/openrouter/prompts.json.");
            Console.WriteLine("  --output <path>         Output directory. Defaults to %APPDATA%/BIBIM/debug/openrouter-bench/...");
            Console.WriteLine("  --model <id>            Run one model id.");
            Console.WriteLine("  --prompt <id>           Run one prompt id.");
            Console.WriteLine("  --max-models <n>        Limit number of models.");
            Console.WriteLine("  --max-prompts <n>       Limit number of prompts.");
            Console.WriteLine("  --revit-version <year>  Revit version for prompt/analyzer. Default 2026.");
            Console.WriteLine("  --revit-path <path>     Folder containing RevitAPI.dll and RevitAPIUI.dll.");
            Console.WriteLine("  --flow <app|codegen>    app runs planner -> task -> codegen. Default app.");
            Console.WriteLine("  --language <kr|en>      Prompt/UI language. Default kr.");
        }
    }

    internal sealed class BenchOptions
    {
        public bool ShowHelp { get; set; }
        public string KeyPath { get; set; }
        public string ModelsPath { get; set; } = "benchmarks/openrouter/models.json";
        public string PromptsPath { get; set; } = "benchmarks/openrouter/prompts.json";
        public string OutputRoot { get; set; }
        public string ModelFilter { get; set; }
        public string PromptFilter { get; set; }
        public string RevitVersion { get; set; } = "2026";
        public string RevitPath { get; set; }
        public string Flow { get; set; } = "app";
        public string Language { get; set; } = "kr";
        public int MaxModels { get; set; }
        public int MaxPrompts { get; set; }

        public static BenchOptions Parse(string[] args)
        {
            var options = new BenchOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : "";

                switch (arg)
                {
                    case "-h":
                    case "--help":
                        options.ShowHelp = true;
                        break;
                    case "--key-path":
                        options.KeyPath = Next();
                        break;
                    case "--models":
                        options.ModelsPath = Next();
                        break;
                    case "--prompts":
                        options.PromptsPath = Next();
                        break;
                    case "--output":
                        options.OutputRoot = Next();
                        break;
                    case "--model":
                        options.ModelFilter = Next();
                        break;
                    case "--prompt":
                        options.PromptFilter = Next();
                        break;
                    case "--max-models":
                        int.TryParse(Next(), out var maxModels);
                        options.MaxModels = maxModels;
                        break;
                    case "--max-prompts":
                        int.TryParse(Next(), out var maxPrompts);
                        options.MaxPrompts = maxPrompts;
                        break;
                    case "--revit-version":
                        options.RevitVersion = Next();
                        break;
                    case "--revit-path":
                        options.RevitPath = Next();
                        break;
                    case "--flow":
                        options.Flow = Next();
                        break;
                    case "--language":
                        options.Language = Next();
                        break;
                }
            }
            return options;
        }
    }

    internal sealed class BenchModel
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Group { get; set; }
        public string ApproxVramClass { get; set; }
        public bool SupportsTools { get; set; }
        public bool Enabled { get; set; }
        public bool NotOnOpenRouter { get; set; }
        public string Notes { get; set; }
    }

    internal sealed class BenchPrompt
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public string Risk { get; set; }
        public bool RequiresRuntimeValidation { get; set; }
        public bool CaptureScreenshot { get; set; }
        public string UserMessage { get; set; }
        public string Prompt { get; set; }
        public List<string> RagQueries { get; set; }
        public string ClarificationAnswer { get; set; }
        public List<string> ExpectedCompileChecks { get; set; }
    }

    internal sealed class ForcedRagBundle
    {
        public string Context { get; set; } = "";
        public int CallCount { get; set; }
        public long ElapsedMs { get; set; }
    }

    internal sealed class PlannerResponseMetric
    {
        public int Attempt { get; set; }
        public bool Success { get; set; }
        public long ElapsedMs { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public string ErrorMessage { get; set; }

        public static PlannerResponseMetric FromResponse(int attempt, LlmResponse response)
        {
            return new PlannerResponseMetric
            {
                Attempt = attempt,
                Success = response?.Success ?? false,
                ElapsedMs = response?.ElapsedMs ?? 0,
                InputTokens = response?.InputTokens ?? 0,
                OutputTokens = response?.OutputTokens ?? 0,
                ErrorMessage = response?.ErrorMessage
            };
        }
    }

    internal sealed class BenchResult
    {
        public string TimestampUtc { get; set; }
        public string FlowMode { get; set; }
        public string ModelId { get; set; }
        public string ModelGroup { get; set; }
        public string ApproxVramClass { get; set; }
        public string PromptId { get; set; }
        public string PromptCategory { get; set; }
        public string Risk { get; set; }
        public bool RequiresRuntimeValidation { get; set; }
        public bool CaptureScreenshot { get; set; }
        public bool Success { get; set; }
        public bool IsCodeResponse { get; set; }
        public bool CompileSuccess { get; set; }
        public int CompileAttempts { get; set; }
        public int LlmTurnCount { get; set; }
        public int ToolCallCount { get; set; }
        public int RagCallCount { get; set; }
        public int ForcedRagCallCount { get; set; }
        public long ForcedRagElapsedMs { get; set; }
        public int RoslynToolCallCount { get; set; }
        public long ElapsedMs { get; set; }
        public long PlannerElapsedMs { get; set; }
        public int PlannerResponseCount { get; set; }
        public string PlannerMode { get; set; }
        public string PlannerTaskKind { get; set; }
        public bool PlannerShouldAutoRun { get; set; }
        public int PlannerQuestionCount { get; set; }
        public int ClarificationRounds { get; set; }
        public long CodegenElapsedMs { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CachedInputTokens { get; set; }
        public int CacheCreationInputTokens { get; set; }
        public int GeneratedCodeChars { get; set; }
        public string ErrorMessage { get; set; }
        public string DebugArtifactDirectory { get; set; }

        public static string CsvHeader =>
            "timestamp_utc,flow_mode,model_id,model_group,approx_vram_class,prompt_id,prompt_category,risk,requires_runtime_validation,capture_screenshot,success,is_code_response,compile_success,compile_attempts,llm_turns,tool_calls,rag_calls,forced_rag_calls,forced_rag_elapsed_ms,roslyn_tool_calls,total_elapsed_ms,planner_elapsed_ms,planner_responses,planner_mode,planner_task_kind,planner_should_auto_run,planner_questions,clarification_rounds,codegen_elapsed_ms,input_tokens,output_tokens,cached_input_tokens,cache_creation_input_tokens,generated_code_chars,error,debug_dir";

        public static BenchResult FromCodegen(
            BenchModel model,
            BenchPrompt prompt,
            string debugDir,
            long elapsedMs,
            CodeGenerationResult result)
        {
            return new BenchResult
            {
                TimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                FlowMode = "codegen",
                ModelId = model.Id,
                ModelGroup = model.Group,
                ApproxVramClass = model.ApproxVramClass,
                PromptId = prompt.Id,
                PromptCategory = prompt.Category,
                Risk = prompt.Risk,
                RequiresRuntimeValidation = prompt.RequiresRuntimeValidation,
                CaptureScreenshot = prompt.CaptureScreenshot,
                Success = result.Success,
                IsCodeResponse = result.IsCodeResponse,
                CompileSuccess = result.CompilationResult?.Success ?? false,
                CompileAttempts = result.CompileAttempts,
                LlmTurnCount = result.LlmTurnCount,
                ToolCallCount = result.ToolCallCount,
                RagCallCount = result.RagCallCount,
                RoslynToolCallCount = result.RoslynToolCallCount,
                ElapsedMs = elapsedMs,
                CodegenElapsedMs = elapsedMs,
                InputTokens = result.TotalInputTokens,
                OutputTokens = result.TotalOutputTokens,
                CachedInputTokens = result.TotalCachedInputTokens,
                CacheCreationInputTokens = result.TotalCacheCreationInputTokens,
                GeneratedCodeChars = result.GeneratedCode?.Length ?? 0,
                ErrorMessage = result.ErrorMessage,
                DebugArtifactDirectory = debugDir
            };
        }

        public static BenchResult FromException(
            BenchModel model,
            BenchPrompt prompt,
            string debugDir,
            long elapsedMs,
            Exception ex)
        {
            return new BenchResult
            {
                TimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                FlowMode = "codegen",
                ModelId = model.Id,
                ModelGroup = model.Group,
                ApproxVramClass = model.ApproxVramClass,
                PromptId = prompt.Id,
                PromptCategory = prompt.Category,
                Risk = prompt.Risk,
                RequiresRuntimeValidation = prompt.RequiresRuntimeValidation,
                CaptureScreenshot = prompt.CaptureScreenshot,
                Success = false,
                CompileSuccess = false,
                ElapsedMs = elapsedMs,
                ErrorMessage = ex.Message,
                DebugArtifactDirectory = debugDir
            };
        }

        public static BenchResult FromPlannerOnly(
            BenchModel model,
            BenchPrompt prompt,
            string debugDir,
            long elapsedMs,
            TaskPlanResponse plan,
            List<PlannerResponseMetric> plannerMetrics)
        {
            return new BenchResult
            {
                TimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                FlowMode = "app",
                ModelId = model.Id,
                ModelGroup = model.Group,
                ApproxVramClass = model.ApproxVramClass,
                PromptId = prompt.Id,
                PromptCategory = prompt.Category,
                Risk = prompt.Risk,
                RequiresRuntimeValidation = prompt.RequiresRuntimeValidation,
                CaptureScreenshot = prompt.CaptureScreenshot,
                Success = false,
                CompileSuccess = false,
                ElapsedMs = elapsedMs,
                PlannerElapsedMs = plannerMetrics?.Sum(m => m.ElapsedMs) ?? 0,
                PlannerResponseCount = plannerMetrics?.Count ?? 0,
                PlannerMode = plan?.Mode,
                PlannerTaskKind = plan?.TaskKind,
                PlannerShouldAutoRun = plan?.ShouldAutoRun ?? false,
                PlannerQuestionCount = plan?.Questions?.Count ?? 0,
                DebugArtifactDirectory = debugDir
            };
        }

        public string ToCsvRow()
        {
            string[] values =
            {
                TimestampUtc,
                FlowMode,
                ModelId,
                ModelGroup,
                ApproxVramClass,
                PromptId,
                PromptCategory,
                Risk,
                RequiresRuntimeValidation.ToString(),
                CaptureScreenshot.ToString(),
                Success.ToString(),
                IsCodeResponse.ToString(),
                CompileSuccess.ToString(),
                CompileAttempts.ToString(CultureInfo.InvariantCulture),
                LlmTurnCount.ToString(CultureInfo.InvariantCulture),
                ToolCallCount.ToString(CultureInfo.InvariantCulture),
                RagCallCount.ToString(CultureInfo.InvariantCulture),
                ForcedRagCallCount.ToString(CultureInfo.InvariantCulture),
                ForcedRagElapsedMs.ToString(CultureInfo.InvariantCulture),
                RoslynToolCallCount.ToString(CultureInfo.InvariantCulture),
                ElapsedMs.ToString(CultureInfo.InvariantCulture),
                PlannerElapsedMs.ToString(CultureInfo.InvariantCulture),
                PlannerResponseCount.ToString(CultureInfo.InvariantCulture),
                PlannerMode,
                PlannerTaskKind,
                PlannerShouldAutoRun.ToString(),
                PlannerQuestionCount.ToString(CultureInfo.InvariantCulture),
                ClarificationRounds.ToString(CultureInfo.InvariantCulture),
                CodegenElapsedMs.ToString(CultureInfo.InvariantCulture),
                InputTokens.ToString(CultureInfo.InvariantCulture),
                OutputTokens.ToString(CultureInfo.InvariantCulture),
                CachedInputTokens.ToString(CultureInfo.InvariantCulture),
                CacheCreationInputTokens.ToString(CultureInfo.InvariantCulture),
                GeneratedCodeChars.ToString(CultureInfo.InvariantCulture),
                ErrorMessage,
                DebugArtifactDirectory
            };

            return string.Join(",", values.Select(Csv));
        }

        private static string Csv(string value)
        {
            value = value ?? "";
            bool quote = value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r");
            value = value.Replace("\"", "\"\"");
            return quote ? $"\"{value}\"" : value;
        }
    }
}
