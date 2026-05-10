using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Bibim.Core;
using Newtonsoft.Json;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingBrushes = System.Drawing.Brushes;
using DrawingColor = System.Drawing.Color;
using DrawingFont = System.Drawing.Font;
using DrawingFontStyle = System.Drawing.FontStyle;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingPen = System.Drawing.Pen;
using DrawingRectangleF = System.Drawing.RectangleF;
using DrawingSolidBrush = System.Drawing.SolidBrush;

namespace Bibim.RevitAutoSmoke
{
    public class AutoSmokeApp : IExternalApplication
    {
        private bool _initialized;
        private bool _completed;
        private int _requestIndex;
        private string _root;
        private string _resultPath;
        private string _logPath;
        private string _activeSourceModelPath;
        private SmokeRequest[] _requests = new SmokeRequest[0];
        private SmokeBatchResult _batchResult;

        public Result OnStartup(UIControlledApplication application)
        {
            application.Idling += OnIdling;
            application.DialogBoxShowing += OnDialogBoxShowing;
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.Idling -= OnIdling;
            application.DialogBoxShowing -= OnDialogBoxShowing;
            return Result.Succeeded;
        }

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            string dialogId = e?.DialogId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(dialogId)) return;

            try
            {
                File.AppendAllText(GetLogPath(), $"[{DateTime.Now:O}] DialogBoxShowing: {dialogId}{Environment.NewLine}");
            }
            catch { }

            if (IsUnresolvedReferencesDialog(dialogId))
            {
                const int secondCommandLink = 1002;
                try
                {
                    bool accepted = e.OverrideResult(secondCommandLink);
                    File.AppendAllText(GetLogPath(),
                        $"[{DateTime.Now:O}] Auto-dismiss unresolved references dialog result={secondCommandLink} accepted={accepted}{Environment.NewLine}");
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.AppendAllText(GetLogPath(),
                            $"[{DateTime.Now:O}] Auto-dismiss unresolved references dialog failed: {ex.Message}{Environment.NewLine}");
                    }
                    catch { }
                }
            }
        }

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            if (_completed) return;

            var uiApp = sender as UIApplication;
            if (uiApp == null) return;

            try
            {
                if (!_initialized)
                {
                    _root = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "BIBIM",
                        "debug",
                        "revit-auto-smoke");
                    Directory.CreateDirectory(_root);

                    var requestPath = Path.Combine(_root, "request.json");
                    _resultPath = Path.Combine(_root, "result.json");
                    _logPath = Path.Combine(_root, "auto_smoke.log");
                    _requests = LoadRequests(_root, requestPath);
                    _batchResult = new SmokeBatchResult
                    {
                        Timestamp = DateTime.Now,
                        RevitVersion = uiApp.Application.VersionNumber
                    };

                    File.AppendAllText(_logPath,
                        $"[{DateTime.Now:O}] Auto smoke started. count={_requests.Length}{Environment.NewLine}");
                    _initialized = true;
                }

                if (_requestIndex >= _requests.Length)
                {
                    WriteBatchResult();
                    File.AppendAllText(_logPath,
                        $"[{DateTime.Now:O}] Auto smoke completed. count={_batchResult.Results.Length}{Environment.NewLine}");
                    _completed = true;
                    return;
                }

                var request = _requests[_requestIndex];
                SmokeResult result;
                try
                {
                    File.AppendAllText(_logPath,
                        $"[{DateTime.Now:O}] Running request {_requestIndex + 1}/{_requests.Length}: {request.ModelId}{Environment.NewLine}");
                    result = RunOne(uiApp, request, _root, _logPath);
                }
                catch (Exception ex)
                {
                    result = new SmokeResult
                    {
                        Id = request.Id,
                        ModelId = request.ModelId,
                        PromptId = request.PromptId,
                        Timestamp = DateTime.Now,
                        RevitVersion = uiApp.Application.VersionNumber,
                        GeneratedCodePath = request.GeneratedCodePath,
                        CompileSuccess = false,
                        ExecutionSuccess = false,
                        ExecutionError = ex.ToString(),
                        OutputDirectory = request.OutputDirectory
                    };
                    File.AppendAllText(_logPath,
                        $"[{DateTime.Now:O}] REQUEST ERROR {request.ModelId}: {ex}{Environment.NewLine}");
                }

                _batchResult.Results = _batchResult.Results.Concat(new[] { result }).ToArray();
                File.WriteAllText(_resultPath, JsonConvert.SerializeObject(result, Formatting.Indented));
                WriteBatchResult();
                _requestIndex++;

                try { e.SetRaiseWithoutDelay(); }
                catch { }
            }
            catch (Exception ex)
            {
                var root = _root ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BIBIM",
                    "debug",
                    "revit-auto-smoke");
                Directory.CreateDirectory(root);

                var logPath = _logPath ?? Path.Combine(root, "auto_smoke.log");
                var resultPath = _resultPath ?? Path.Combine(root, "result.json");
                File.AppendAllText(logPath, $"[{DateTime.Now:O}] ERROR {ex}{Environment.NewLine}");
                File.WriteAllText(resultPath, JsonConvert.SerializeObject(new SmokeResult
                {
                    Timestamp = DateTime.Now,
                    CompileSuccess = false,
                    ExecutionSuccess = false,
                    ExecutionError = ex.ToString()
                }, Formatting.Indented));
                _completed = true;
            }
        }

        private void WriteBatchResult()
        {
            if (string.IsNullOrWhiteSpace(_root) || _batchResult == null) return;

            File.WriteAllText(Path.Combine(_root, "batch_result.json"),
                JsonConvert.SerializeObject(_batchResult, Formatting.Indented));
            if (!string.IsNullOrWhiteSpace(_resultPath))
            {
                File.WriteAllText(_resultPath, JsonConvert.SerializeObject(_batchResult, Formatting.Indented));
            }
        }

        private string GetLogPath()
        {
            if (!string.IsNullOrWhiteSpace(_logPath)) return _logPath;

            string root = !string.IsNullOrWhiteSpace(_root)
                ? _root
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BIBIM",
                    "debug",
                    "revit-auto-smoke");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "auto_smoke.log");
        }

        private static bool IsUnresolvedReferencesDialog(string dialogId)
        {
            return dialogId.IndexOf("Unresolved", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (dialogId.IndexOf("Reference", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 dialogId.IndexOf("Link", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static SmokeRequest[] LoadRequests(string root, string requestPath)
        {
            string batchPath = Path.Combine(root, "batch_requests.json");
            if (File.Exists(batchPath))
            {
                var batch = JsonConvert.DeserializeObject<SmokeBatchRequest>(File.ReadAllText(batchPath));
                if (batch?.Requests != null && batch.Requests.Length > 0)
                    return batch.Requests;
            }

            var request = File.Exists(requestPath)
                ? JsonConvert.DeserializeObject<SmokeRequest>(File.ReadAllText(requestPath))
                : new SmokeRequest();
            return new[] { request };
        }

        private SmokeResult RunOne(UIApplication uiApp, SmokeRequest request, string root, string logPath)
        {
            if (string.IsNullOrWhiteSpace(request.GeneratedCodePath) || !File.Exists(request.GeneratedCodePath))
                throw new FileNotFoundException("Generated code file not found.", request.GeneratedCodePath);

            var outputDir = string.IsNullOrWhiteSpace(request.OutputDirectory)
                ? Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss"))
                : request.OutputDirectory;
            Directory.CreateDirectory(outputDir);

            var doc = EnsureDocument(uiApp, request, outputDir, logPath);
            if (doc == null)
                throw new InvalidOperationException("No active Revit document is available.");

            PrepareSelection(uiApp.ActiveUIDocument, request, logPath);

            string code = File.ReadAllText(request.GeneratedCodePath);
            var compiler = new RoslynCompilerService();
            var compileResult = compiler.Compile(code);
            File.WriteAllText(Path.Combine(outputDir, "compile_wrapped.cs"), compileResult.WrappedSource ?? "");
            File.WriteAllText(Path.Combine(outputDir, "compile_diagnostics.txt"), compileResult.ErrorSummary ?? "");

            object generatedOutput = null;
            var ctx = new BibimExecutionContext();
            bool executionSuccess = false;
            string executionError = null;

            if (compileResult.Success && compileResult.Assembly != null)
            {
                try
                {
                    generatedOutput = InvokeGeneratedCode(compileResult.Assembly, uiApp, ctx);
                    executionSuccess = true;
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null
                        ? tie.InnerException
                        : ex;
                    executionError = inner.ToString();
                }
            }

            try { doc.Regenerate(); }
            catch { }

            string imagePath = null;
            string exportViewName = null;
            if (request.CaptureScreenshot)
            {
                imagePath = TryExportBenchmarkView(
                    doc,
                    request,
                    outputDir,
                    logPath,
                    compileResult.Success,
                    executionSuccess,
                    generatedOutput?.ToString(),
                    ctx.GetLogs(),
                    out exportViewName);
            }
            else
            {
                File.AppendAllText(logPath,
                    $"Screenshot skipped for {request.PromptId}: CaptureScreenshot=false{Environment.NewLine}");
            }
            try { doc.Save(); }
            catch (Exception ex) { File.AppendAllText(logPath, $"Save after run failed: {ex.Message}{Environment.NewLine}"); }

            var result = new SmokeResult
            {
                Id = request.Id,
                ModelId = request.ModelId,
                PromptId = request.PromptId,
                Timestamp = DateTime.Now,
                RevitVersion = uiApp.Application.VersionNumber,
                DocumentTitle = doc.Title,
                SourceModelPath = request.SourceModelPath,
                ActiveViewName = uiApp.ActiveUIDocument?.ActiveView?.Name,
                CaptureScreenshot = request.CaptureScreenshot,
                ExportViewName = exportViewName,
                GeneratedCodePath = request.GeneratedCodePath,
                CompileSuccess = compileResult.Success,
                CompileError = compileResult.ErrorSummary,
                ExecutionSuccess = executionSuccess,
                ExecutionError = executionError,
                GeneratedOutput = generatedOutput?.ToString(),
                Logs = ctx.GetLogs().ToArray(),
                SelectedElementIds = uiApp.ActiveUIDocument?.Selection?.GetElementIds()?.Select(id => id.Value).ToArray(),
                OutputDirectory = outputDir,
                ImagePath = imagePath
            };

            File.WriteAllText(Path.Combine(outputDir, "result.json"), JsonConvert.SerializeObject(result, Formatting.Indented));
            return result;
        }

        private Document EnsureDocument(UIApplication uiApp, SmokeRequest request, string outputDir, string logPath)
        {
            if (!string.IsNullOrWhiteSpace(request.SourceModelPath))
            {
                if (!File.Exists(request.SourceModelPath))
                    throw new FileNotFoundException("Source model file not found.", request.SourceModelPath);

                string sourceExtension = Path.GetExtension(request.SourceModelPath);
                if (string.IsNullOrWhiteSpace(sourceExtension))
                    sourceExtension = ".rvt";

                string copiedModelPath = Path.Combine(outputDir, "auto_smoke_project" + sourceExtension);
                File.Copy(request.SourceModelPath, copiedModelPath, overwrite: true);
                File.SetAttributes(copiedModelPath, File.GetAttributes(copiedModelPath) & ~FileAttributes.ReadOnly);

                var activeUidoc = uiApp.ActiveUIDocument;
                if (!request.ForceNewDocument &&
                    activeUidoc?.Document != null &&
                    activeUidoc.Document.IsValidObject &&
                    IsSamePath(_activeSourceModelPath, request.SourceModelPath))
                {
                    ActivateRequestedView(activeUidoc, request.ViewName, logPath);
                    File.AppendAllText(logPath,
                        $"Reusing active source model for batch request: {activeUidoc.Document.PathName}{Environment.NewLine}");
                    File.AppendAllText(logPath, $"Active view: {activeUidoc.ActiveView?.Name}{Environment.NewLine}");
                    return activeUidoc.Document;
                }

                var sourceUidoc = uiApp.OpenAndActivateDocument(copiedModelPath);
                if (sourceUidoc == null)
                    throw new InvalidOperationException("Failed to open copied source model.");

                _activeSourceModelPath = Path.GetFullPath(request.SourceModelPath);
                ActivateRequestedView(sourceUidoc, request.ViewName, logPath);
                File.AppendAllText(logPath, $"Activated copied source model: {copiedModelPath}{Environment.NewLine}");
                File.AppendAllText(logPath, $"Active view: {sourceUidoc.ActiveView?.Name}{Environment.NewLine}");
                return sourceUidoc.Document;
            }

            var app = uiApp.Application;
            string template = null;
            try { template = app.DefaultProjectTemplate; }
            catch { }

            Document doc;
            if (!string.IsNullOrWhiteSpace(template) && File.Exists(template))
            {
                File.AppendAllText(logPath, $"Using template: {template}{Environment.NewLine}");
                doc = app.NewProjectDocument(template);
            }
            else
            {
                File.AppendAllText(logPath, "Using metric empty project document fallback." + Environment.NewLine);
                doc = app.NewProjectDocument(UnitSystem.Metric);
            }

            string modelPath = Path.Combine(outputDir, "auto_smoke_project.rvt");
            var saveAs = new SaveAsOptions
            {
                OverwriteExistingFile = true
            };
            doc.SaveAs(modelPath, saveAs);
            doc.Close(false);

            var uidoc = uiApp.OpenAndActivateDocument(modelPath);
            ActivateRequestedView(uidoc, request.ViewName, logPath);
            File.AppendAllText(logPath, $"Activated document: {modelPath}{Environment.NewLine}");
            File.AppendAllText(logPath, $"Active view: {uidoc?.ActiveView?.Name}{Environment.NewLine}");
            return uidoc?.Document;
        }

        private static bool IsSamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try
            {
                left = Path.GetFullPath(left);
                right = Path.GetFullPath(right);
            }
            catch { }

            return string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static void ActivateRequestedView(UIDocument uidoc, string viewName, string logPath)
        {
            if (uidoc == null) return;
            var doc = uidoc.Document;

            View target = null;
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted)
                .ToList();

            if (!string.IsNullOrWhiteSpace(viewName))
            {
                target = views.FirstOrDefault(v => string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                    ?? views.FirstOrDefault(v => v.Name.IndexOf(viewName, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            target = target
                ?? views.OfType<ViewPlan>().FirstOrDefault(v => v.ViewType == ViewType.FloorPlan)
                ?? views.FirstOrDefault(v => v.ViewType == ViewType.ThreeD)
                ?? views.FirstOrDefault();

            if (target == null) return;

            try
            {
                uidoc.ActiveView = target;
                File.AppendAllText(logPath, $"Requested active view: {target.Name}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, $"View activation failed for {target.Name}: {ex.Message}{Environment.NewLine}");
            }
        }

        private static void PrepareSelection(UIDocument uidoc, SmokeRequest request, string logPath)
        {
            if (uidoc == null || request.SelectElementCount <= 0) return;

            var doc = uidoc.Document;
            var selected = new List<ElementId>();
            var categoryNames = request.SelectionCategories != null && request.SelectionCategories.Length > 0
                ? request.SelectionCategories
                : new[] { "OST_Walls", "OST_Doors", "OST_Windows" };

            foreach (var categoryName in categoryNames)
            {
                if (!Enum.TryParse(categoryName, out BuiltInCategory bic)) continue;
                var ids = new FilteredElementCollector(doc)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType()
                    .Where(e => MatchesSelectionFilters(doc, e, request))
                    .Select(e => e.Id)
                    .Where(id => id != ElementId.InvalidElementId)
                    .Take(Math.Max(0, request.SelectElementCount - selected.Count))
                    .ToList();

                selected.AddRange(ids);
                if (selected.Count >= request.SelectElementCount) break;
            }

            if (selected.Count == 0)
            {
                File.AppendAllText(logPath, "Selection precondition found no elements." + Environment.NewLine);
                return;
            }

            uidoc.Selection.SetElementIds(selected);
            File.AppendAllText(logPath, $"Preselected elements: {string.Join(", ", selected.Select(id => id.Value))}{Environment.NewLine}");
        }

        private static bool MatchesSelectionFilters(Document doc, Element element, SmokeRequest request)
        {
            if (element == null) return false;

            if (!ContainsIgnoreCase(element.Name, request.SelectionNameContains))
                return false;

            if (!string.IsNullOrWhiteSpace(request.SelectionTypeNameContains))
            {
                string typeName = null;
                try
                {
                    var typeId = element.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                        typeName = doc.GetElement(typeId)?.Name;
                }
                catch { }

                if (!ContainsIgnoreCase(typeName, request.SelectionTypeNameContains))
                    return false;
            }

            return true;
        }

        private static bool ContainsIgnoreCase(string value, string requiredSubstring)
        {
            if (string.IsNullOrWhiteSpace(requiredSubstring)) return true;
            return !string.IsNullOrWhiteSpace(value) &&
                value.IndexOf(requiredSubstring, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object InvokeGeneratedCode(Assembly assembly, UIApplication uiApp, BibimExecutionContext ctx)
        {
            var type = assembly.GetType("BibimGenerated.Program") ??
                assembly.GetTypes().FirstOrDefault(t =>
                    t.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static) != null);

            if (type == null)
                throw new InvalidOperationException("Generated entry type not found.");

            var method = type.GetMethod(
                "Execute",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(UIApplication), typeof(BibimExecutionContext) },
                null);

            if (method != null)
                return method.Invoke(null, new object[] { uiApp, ctx });

            method = type.GetMethod(
                "Execute",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(UIApplication) },
                null);

            if (method == null)
                throw new InvalidOperationException("Generated Execute method not found.");

            return method.Invoke(null, new object[] { uiApp });
        }

        private static string TryExportBenchmarkView(
            Document doc,
            SmokeRequest request,
            string outputDir,
            string logPath,
            bool compileSuccess,
            bool executionSuccess,
            string generatedOutput,
            IEnumerable<string> executionLogs,
            out string exportViewName)
        {
            exportViewName = null;
            try
            {
                string prefix = Path.Combine(outputDir, "active_view");
                var before = Directory.GetFiles(outputDir, "*.png", SearchOption.TopDirectoryOnly);
                var exportView = GetBenchmarkExportView(doc, request.ViewName, logPath, writeLog: true);
                if (exportView == null)
                    throw new InvalidOperationException("No printable model view was found for image export.");
                exportViewName = exportView.Name;

                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = prefix,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    PixelSize = 1800,
                    FitDirection = FitDirectionType.Horizontal
                };
                options.SetViewsAndSheets(new List<ElementId> { exportView.Id });
                doc.ExportImage(options);

                var after = Directory.GetFiles(outputDir, "*.png", SearchOption.TopDirectoryOnly)
                    .Except(before, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();

                return TryAnnotateImage(
                    after,
                    request,
                    exportView,
                    compileSuccess,
                    executionSuccess,
                    generatedOutput,
                    executionLogs,
                    logPath) ?? after;
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, $"Image export failed: {ex}{Environment.NewLine}");
                return null;
            }
        }

        private static View GetBenchmarkExportView(Document doc, string requestedViewName, string logPath, bool writeLog)
        {
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted && v.ViewType != ViewType.DrawingSheet)
                .ToList();

            if (!string.IsNullOrWhiteSpace(requestedViewName))
            {
                var requested = views.FirstOrDefault(v => string.Equals(v.Name, requestedViewName, StringComparison.OrdinalIgnoreCase))
                    ?? views.FirstOrDefault(v => v.Name.IndexOf(requestedViewName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (requested != null)
                {
                    if (writeLog) File.AppendAllText(logPath, $"Export view: {requested.Name}{Environment.NewLine}");
                    return requested;
                }
            }

            var target = views.FirstOrDefault(v => v.ViewType == ViewType.ThreeD && !v.Name.StartsWith("{", StringComparison.Ordinal))
                ?? views.FirstOrDefault(v => v.ViewType == ViewType.ThreeD)
                ?? views.OfType<ViewPlan>().FirstOrDefault(v => v.ViewType == ViewType.FloorPlan)
                ?? views.FirstOrDefault(v => v.ViewType != ViewType.Legend && v.ViewType != ViewType.Schedule)
                ?? views.FirstOrDefault();

            if (writeLog && target != null)
                File.AppendAllText(logPath, $"Export view: {target.Name}{Environment.NewLine}");

            return target;
        }

        private static string TryAnnotateImage(
            string imagePath,
            SmokeRequest request,
            View exportView,
            bool compileSuccess,
            bool executionSuccess,
            string generatedOutput,
            IEnumerable<string> executionLogs,
            string logPath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return imagePath;

            try
            {
                string annotatedPath = Path.Combine(Path.GetDirectoryName(imagePath) ?? "", "active_view_annotated.png");
                using (var bitmap = new DrawingBitmap(imagePath))
                using (var graphics = DrawingGraphics.FromImage(bitmap))
                using (var titleFont = new DrawingFont("Segoe UI", Math.Max(22, bitmap.Width / 58f), DrawingFontStyle.Bold))
                using (var bodyFont = new DrawingFont("Segoe UI", Math.Max(18, bitmap.Width / 72f), DrawingFontStyle.Regular))
                using (var smallFont = new DrawingFont("Segoe UI", Math.Max(15, bitmap.Width / 88f), DrawingFontStyle.Regular))
                using (var panelBrush = new DrawingSolidBrush(DrawingColor.FromArgb(232, 255, 255, 255)))
                using (var borderPen = new DrawingPen(DrawingColor.FromArgb(210, 40, 40, 40), 2f))
                using (var titleBrush = new DrawingSolidBrush(DrawingColor.FromArgb(255, 20, 20, 20)))
                using (var bodyBrush = new DrawingSolidBrush(DrawingColor.FromArgb(255, 30, 30, 30)))
                using (var statusBrush = new DrawingSolidBrush(compileSuccess && executionSuccess
                    ? DrawingColor.FromArgb(255, 0, 110, 55)
                    : DrawingColor.FromArgb(255, 170, 40, 30)))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                    float margin = Math.Max(24, bitmap.Width * 0.018f);
                    float panelWidth = Math.Min(bitmap.Width - margin * 2, Math.Max(720, bitmap.Width * 0.48f));
                    float maxTextWidth = panelWidth - margin * 1.2f;
                    string title = $"{request.PromptId ?? "prompt"}";
                    string status = $"compile={(compileSuccess ? "OK" : "FAIL")}  revit={(executionSuccess ? "OK" : "FAIL")}  view={exportView?.Name}";
                    string output = CollapseWhitespace(generatedOutput);
                    if (string.IsNullOrWhiteSpace(output) && executionLogs != null)
                        output = CollapseWhitespace(string.Join(" / ", executionLogs.Where(l => !string.IsNullOrWhiteSpace(l)).Take(3)));
                    if (string.IsNullOrWhiteSpace(output))
                        output = "(no runtime output)";

                    output = Truncate(output, 460);
                    var outputSize = graphics.MeasureString(output, bodyFont, (int)maxTextWidth);
                    float panelHeight = margin * 1.4f + titleFont.Height + smallFont.Height + outputSize.Height + bodyFont.Height;
                    float x = margin;
                    float y = bitmap.Height - panelHeight - margin;
                    if (y < margin) y = margin;

                    var rect = new DrawingRectangleF(x, y, panelWidth, panelHeight);
                    graphics.FillRectangle(panelBrush, rect);
                    graphics.DrawRectangle(borderPen, x, y, panelWidth, panelHeight);

                    float textX = x + margin * 0.6f;
                    float textY = y + margin * 0.45f;
                    graphics.DrawString(title, titleFont, titleBrush, textX, textY);
                    textY += titleFont.Height + 4;
                    graphics.DrawString(status, smallFont, statusBrush, textX, textY);
                    textY += smallFont.Height + 8;
                    graphics.DrawString(output, bodyFont, bodyBrush, new DrawingRectangleF(textX, textY, maxTextWidth, panelHeight - (textY - y)));
                    bitmap.Save(annotatedPath, ImageFormat.Png);
                }

                return annotatedPath;
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, $"Image annotation failed: {ex.Message}{Environment.NewLine}");
                return imagePath;
            }
        }

        private static string CollapseWhitespace(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var builder = new StringBuilder(value.Length);
            bool lastWasSpace = false;
            foreach (char ch in value)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!lastWasSpace)
                    {
                        builder.Append(' ');
                        lastWasSpace = true;
                    }
                }
                else
                {
                    builder.Append(ch);
                    lastWasSpace = false;
                }
            }
            return builder.ToString().Trim();
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }
    }

    public class SmokeRequest
    {
        public string Id { get; set; }
        public string ModelId { get; set; }
        public string PromptId { get; set; }
        public string GeneratedCodePath { get; set; }
        public string SourceModelPath { get; set; }
        public string ViewName { get; set; }
        public bool CaptureScreenshot { get; set; }
        public bool ForceNewDocument { get; set; }
        public int SelectElementCount { get; set; }
        public string[] SelectionCategories { get; set; }
        public string SelectionNameContains { get; set; }
        public string SelectionTypeNameContains { get; set; }
        public string OutputDirectory { get; set; }
    }

    public class SmokeBatchRequest
    {
        public SmokeRequest[] Requests { get; set; } = new SmokeRequest[0];
    }

    public class SmokeBatchResult
    {
        public DateTime Timestamp { get; set; }
        public string RevitVersion { get; set; }
        public SmokeResult[] Results { get; set; } = new SmokeResult[0];
    }

    public class SmokeResult
    {
        public string Id { get; set; }
        public string ModelId { get; set; }
        public string PromptId { get; set; }
        public DateTime Timestamp { get; set; }
        public string RevitVersion { get; set; }
        public string DocumentTitle { get; set; }
        public string SourceModelPath { get; set; }
        public string ActiveViewName { get; set; }
        public bool CaptureScreenshot { get; set; }
        public string ExportViewName { get; set; }
        public string GeneratedCodePath { get; set; }
        public bool CompileSuccess { get; set; }
        public string CompileError { get; set; }
        public bool ExecutionSuccess { get; set; }
        public string ExecutionError { get; set; }
        public string GeneratedOutput { get; set; }
        public string[] Logs { get; set; }
        public long[] SelectedElementIds { get; set; }
        public string OutputDirectory { get; set; }
        public string ImagePath { get; set; }
    }
}
