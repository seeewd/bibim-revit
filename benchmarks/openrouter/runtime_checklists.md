# OpenRouter Benchmark Runtime Checklists

Use a disposable Revit model for all mutating prompts. Record pass/fail manually in
the benchmark result CSV/JSON after each run.

## selection_report

Preconditions:
- Open any model with at least two selectable elements.
- Select two or more elements before running generated code.

Expected:
- No model changes.
- Return text includes selected count, ElementId, category, name, and type name.
- Empty selection returns a clear "select elements first" message.

## selected_mark_parameter

Preconditions:
- Use a disposable model.
- Select elements that expose writable Mark parameters.

Expected:
- One transaction is used.
- Writable Mark values become `BIBIM_TEST`.
- Missing/read-only parameters are skipped without crashing.
- Return/log text includes changed and skipped counts.

Cleanup:
- Undo the transaction or restore the disposable model.

## active_view_textnote

Preconditions:
- Active view must support text notes.
- A text note type must exist.

Expected:
- A text note with text `BIBIM TEST NOTE` appears in the active view.
- Return text includes the new element id.
- Missing text note type is handled cleanly.

Cleanup:
- Delete created note or undo.

## wall_door_window_counts

Preconditions:
- Open a model with walls, doors, and windows if possible.

Expected:
- No model changes.
- Counts match a quick manual check or Revit schedule/filter sanity check.
- Instance collectors do not include element types.

## level_summary

Preconditions:
- Open a model with multiple levels.

Expected:
- No model changes.
- Levels are sorted by elevation.
- Output includes name, ElementId, and elevation.

## parameter_lookup_safe

Preconditions:
- Select elements with and without Comments values if possible.

Expected:
- No model changes.
- Output reports values for elements with the parameter.
- Missing parameters and empty selection are handled cleanly.

## rag_pdf_export_options

Preconditions:
- No model-specific setup required.

Expected:
- No file export occurs.
- Code compiles against the installed Revit API.
- Output is diagnostic only.

## view_sheet_count

Preconditions:
- Open a model with sheets and multiple views if possible.

Expected:
- No model changes.
- Output includes sheet count and non-template view count.
