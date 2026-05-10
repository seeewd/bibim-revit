# BIBIM Revit 2024 LLM Guide Sweep Report

- Date: 2026-05-10
- Scope: guide LV1-LV5, 15 prompts, 1 repeat per model
- Revit: Autodesk Revit 2024
- Model file: Snowdon Towers Sample Architectural.rvt
- Runtime validation: compile-success cases only, each prompt in a fresh Revit process/copy
- Note: this is a 1x sweep, not the guide final 3-repeat average.
- First-attempt success is proxied as runtime success without a Roslyn/code-repair retry. Context/RAG tool calls are not counted as retries.
- API/Code Error excludes planner JSON failures and OpenRouter provider/tool-routing failures.

## Availability Notes
- `anthropic/claude-sonnet-4.6` was used as the Sonnet 4.6 baseline because OpenRouter exposes that exact current ID.
- `mistralai/codestral-2501`, `qwen/qwen-2.5-coder-14b-instruct`, and `qwen/qwen-2.5-coder-7b-instruct` were not available as exact OpenRouter IDs at test time.
- `mistralai/codestral-2508` was used as the nearest Codestral substitute. `google/gemma-4-26b-a4b-it` was included from the Revit 2024 pilot request.

## Overall
| Model | Compile | First Attempt | Runtime | Avg Response (s) | API/Code Error |
|---|---:|---:|---:|---:|---:|
| Claude Sonnet 4.6 (baseline) | 15/15 (100%) | 6/15 (40%) | 15/15 (100%) | 43.5 | 0/15 (0%) |
| Qwen2.5-Coder 32B | 0/15 (0%) | 0/15 (0%) | 0/15 (0%) | 10.2 | 0/15 (0%) |
| Codestral 2508* | 12/15 (80%) | 6/15 (40%) | 10/15 (66.7%) | 14.4 | 5/15 (33.3%) |
| Llama 3.3 70B | 14/15 (93.3%) | 5/15 (33.3%) | 12/15 (80%) | 22.7 | 3/15 (20%) |
| Phi-4 | 0/15 (0%) | 0/15 (0%) | 0/15 (0%) | 10.2 | 0/15 (0%) |
| Gemma4 26B A4B | 15/15 (100%) | 9/15 (60%) | 14/15 (93.3%) | 85.6 | 1/15 (6.7%) |

## LV1
| Model | Compile Success | First Attempt Success | Runtime Success | Avg Response (s) | API/Code Error |
|---|---:|---:|---:|---:|---:|
| Claude Sonnet 4.6 (baseline) | 100% | 100% | 100% | 18.6 | 0% |
| Qwen2.5-Coder 32B | 0% | 0% | 0% | 9.8 | 0% |
| Codestral 2508* | 100% | 66.7% | 100% | 5.3 | 0% |
| Llama 3.3 70B | 100% | 33.3% | 100% | 15.6 | 0% |
| Phi-4 | 0% | 0% | 0% | 6.2 | 0% |
| Gemma4 26B A4B | 100% | 100% | 100% | 40.7 | 0% |

## LV2
| Model | Compile Success | First Attempt Success | Runtime Success | Avg Response (s) | API/Code Error |
|---|---:|---:|---:|---:|---:|
| Claude Sonnet 4.6 (baseline) | 100% | 100% | 100% | 13.5 | 0% |
| Qwen2.5-Coder 32B | 0% | 0% | 0% | 6.1 | 0% |
| Codestral 2508* | 100% | 66.7% | 100% | 3.5 | 0% |
| Llama 3.3 70B | 100% | 33.3% | 100% | 9.2 | 0% |
| Phi-4 | 0% | 0% | 0% | 8.0 | 0% |
| Gemma4 26B A4B | 100% | 100% | 100% | 41.4 | 0% |

## LV3
| Model | Compile Success | First Attempt Success | Runtime Success | Avg Response (s) | API/Code Error |
|---|---:|---:|---:|---:|---:|
| Claude Sonnet 4.6 (baseline) | 100% | 0% | 100% | 33.3 | 0% |
| Qwen2.5-Coder 32B | 0% | 0% | 0% | 6.3 | 0% |
| Codestral 2508* | 66.7% | 33.3% | 33.3% | 13.6 | 66.7% |
| Llama 3.3 70B | 100% | 66.7% | 66.7% | 6.7 | 33.3% |
| Phi-4 | 0% | 0% | 0% | 12.8 | 0% |
| Gemma4 26B A4B | 100% | 33.3% | 100% | 41.4 | 0% |

## LV4
| Model | Compile Success | First Attempt Success | Runtime Success | Avg Response (s) | API/Code Error |
|---|---:|---:|---:|---:|---:|
| Claude Sonnet 4.6 (baseline) | 100% | 0% | 100% | 58.7 | 0% |
| Qwen2.5-Coder 32B | 0% | 0% | 0% | 13.5 | 0% |
| Codestral 2508* | 33.3% | 33.3% | 33.3% | 34.6 | 66.7% |
| Llama 3.3 70B | 66.7% | 33.3% | 66.7% | 44.4 | 33.3% |
| Phi-4 | 0% | 0% | 0% | 10.1 | 0% |
| Gemma4 26B A4B | 100% | 66.7% | 100% | 189.7 | 0% |

## LV5
| Model | Compile Success | First Attempt Success | Runtime Success | Avg Response (s) | API/Code Error |
|---|---:|---:|---:|---:|---:|
| Claude Sonnet 4.6 (baseline) | 100% | 0% | 100% | 93.4 | 0% |
| Qwen2.5-Coder 32B | 0% | 0% | 0% | 15.2 | 0% |
| Codestral 2508* | 100% | 0% | 66.7% | 15.2 | 33.3% |
| Llama 3.3 70B | 100% | 0% | 66.7% | 37.3 | 33.3% |
| Phi-4 | 0% | 0% | 0% | 13.8 | 0% |
| Gemma4 26B A4B | 100% | 0% | 66.7% | 115.0 | 33.3% |

## Failure Highlights
- Gemma4 26B A4B / guide_lv5_p5a_level_wall_area_summary: compile=True, runtime=False, reason=No error text recorded
- Llama 3.3 70B / guide_lv3_p3b_wall_type_total_length: compile=True, runtime=False, reason=System.NullReferenceException: 개체 참조가 개체의 인스턴스로 설정되지 않았습니다.
- Llama 3.3 70B / guide_lv4_p4c_selected_curtain_wall_panels: compile=False, runtime=False, reason=Final compilation failed:
[CS8121] Line 45: 'Element' 형식의 식을 'CurtainGrid' 형식의 패턴으로 처리할 수 없습니다.
- Llama 3.3 70B / guide_lv5_p5a_level_wall_area_summary: compile=True, runtime=False, reason=Autodesk.Revit.Exceptions.ArgumentException: viewId is not a view.
- Phi-4 / guide_lv1_p1a_level_list: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv1_p1b_active_view_category_counts: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv1_p1c_selected_element_parameters: compile=False, runtime=False, reason=Planner routed the prompt to chat mode.
- Phi-4 / guide_lv2_p2a_selected_wall_comments: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv2_p2b_select_doors_active_view: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv2_p2c_create_rf_level: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv3_p3a_window_fire_rating: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv3_p3b_wall_type_total_length: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv3_p3c_select_rooms_area_20: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv4_p4a_empty_room_department: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv4_p4b_column_height_groups: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv4_p4c_selected_curtain_wall_panels: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv5_p5a_level_wall_area_summary: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv5_p5b_grid_intersections_place_columns: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Phi-4 / guide_lv5_p5c_room_compactness_ratio: compile=False, runtime=False, reason=API request failed: OpenRouter API 404: {"error":{"message":"No endpoints found that support tool use. Try disabling \"search_revit_api\". To learn more about p...
- Codestral 2508* / guide_lv3_p3b_wall_type_total_length: compile=True, runtime=False, reason=System.InvalidCastException: 'Autodesk.Revit.DB.FamilyInstance' 형식 개체를 'Autodesk.Revit.DB.Wall' 형식으로 캐스팅할 수 없습니다.
- Codestral 2508* / guide_lv3_p3c_select_rooms_area_20: compile=False, runtime=False, reason=Final compilation failed:
[CS0103] Line 52: 'largeRooms' 이름이 현재 컨텍스트에 없습니다.
- Codestral 2508* / guide_lv4_p4b_column_height_groups: compile=False, runtime=False, reason=Final compilation failed:
[CS0019] Line 51: '&&' 연산자는 'bool' 및 '메서드 그룹' 형식의 피연산자에 적용할 수 없습니다.
[CS1061] Line 57: 'StructuralSection'에는 'get_Parameter'에 대한 정의가 포함...
- Codestral 2508* / guide_lv4_p4c_selected_curtain_wall_panels: compile=False, runtime=False, reason=Final compilation failed:
[CS1061] Line 48: 'object'에는 'GetPanelIds'에 대한 정의가 포함되어 있지 않고, 'object' 형식의 첫 번째 인수를 허용하는 액세스 가능한 확장 메서드 'GetPanelIds'이(가) 없습니다. using...
- Codestral 2508* / guide_lv5_p5a_level_wall_area_summary: compile=True, runtime=False, reason=System.InvalidCastException: 'Autodesk.Revit.DB.FaceWall' 형식 개체를 'Autodesk.Revit.DB.Wall' 형식으로 캐스팅할 수 없습니다.
- Qwen2.5-Coder 32B / guide_lv1_p1a_level_list: compile=False, runtime=False, reason=Task planner returned non-JSON content.
- Qwen2.5-Coder 32B / guide_lv1_p1b_active_view_category_counts: compile=False, runtime=False, reason=Task planner returned non-JSON content.
- Qwen2.5-Coder 32B / guide_lv1_p1c_selected_element_parameters: compile=False, runtime=False, reason=Task planner returned non-JSON content.
- Qwen2.5-Coder 32B / guide_lv2_p2a_selected_wall_comments: compile=False, runtime=False, reason=Unexpected character encountered while parsing value: }. Path 'shouldAutoRun', line 11, position 1.
- Qwen2.5-Coder 32B / guide_lv2_p2b_select_doors_active_view: compile=False, runtime=False, reason=Task planner returned non-JSON content.
- Qwen2.5-Coder 32B / guide_lv2_p2c_create_rf_level: compile=False, runtime=False, reason=Task planner returned non-JSON content.
- Qwen2.5-Coder 32B / guide_lv3_p3a_window_fire_rating: compile=False, runtime=False, reason=Task planner returned non-JSON content.
- Qwen2.5-Coder 32B / guide_lv3_p3b_wall_type_total_length: compile=False, runtime=False, reason=Unexpected character encountered while parsing value: }. Path 'shouldAutoRun', line 17, position 1.
- Qwen2.5-Coder 32B / guide_lv3_p3c_select_rooms_area_20: compile=False, runtime=False, reason=Task planner returned non-JSON content.
- Qwen2.5-Coder 32B / guide_lv4_p4a_empty_room_department: compile=False, runtime=False, reason=Unexpected character encountered while parsing value: }. Path 'shouldAutoRun', line 22, position 1.
- Qwen2.5-Coder 32B / guide_lv4_p4b_column_height_groups: compile=False, runtime=False, reason=Unexpected character encountered while parsing value: }. Path 'shouldAutoRun', line 17, position 1.
- Qwen2.5-Coder 32B / guide_lv4_p4c_selected_curtain_wall_panels: compile=False, runtime=False, reason=Unexpected character encountered while parsing value: }. Path 'shouldAutoRun', line 17, position 1.
- Qwen2.5-Coder 32B / guide_lv5_p5a_level_wall_area_summary: compile=False, runtime=False, reason=Unexpected character encountered while parsing value: }. Path 'shouldAutoRun', line 26, position 1.
- Qwen2.5-Coder 32B / guide_lv5_p5b_grid_intersections_place_columns: compile=False, runtime=False, reason=Task planner returned non-JSON content.
- Qwen2.5-Coder 32B / guide_lv5_p5c_room_compactness_ratio: compile=False, runtime=False, reason=Unexpected character encountered while parsing value: }. Path 'shouldAutoRun', line 23, position 1.

## Recommendation
- Baseline: Claude Sonnet 4.6 passed 15/15 runtime in this 1x sweep.
- Best local-ish candidate in this run: Gemma4 26B A4B, with 15/15 compile and 14/15 runtime, but latency is high and LV5-A failed at runtime compile due `System.Text.Json` reference usage.
- Strong hosted/high-spec candidate: Llama 3.3 70B, with 14/15 compile and 12/15 runtime. It first drops below full success at LV3.
- Codestral 2508 is fast and useful through LV2, but first fails at LV3 and is not stable for LV4+ in this app-flow.
- Qwen2.5-Coder 32B failed the BIBIM planner JSON contract in all prompts. Phi-4 failed OpenRouter tool-use routing in most prompts. They are not usable in the current app-flow without provider/prompt adaptations.

## Next Step
- For the formal guide result, run 3 repeats only for Claude, Gemma4, Llama 70B, and optionally Codestral through LV1-LV3. Exclude Qwen2.5-Coder 32B and Phi-4 unless the planner/tool-use path is changed.

## Post-Test Fixes Applied
- Added code-generation guardrails to avoid `System.Text.Json` in Revit 2024 runtime-compiled code.
- Added wall collector/casting rules to avoid casting non-Wall `OST_Walls` elements such as FamilyInstance or FaceWall.
- Added a level collector rule to prevent passing `Level.Id` as a view-scoped `FilteredElementCollector` id.
