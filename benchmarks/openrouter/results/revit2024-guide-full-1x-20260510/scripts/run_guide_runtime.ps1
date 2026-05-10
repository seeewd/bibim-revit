param(
  [Parameter(Mandatory = $true)]
  [string]$RunRoot,

  [string]$RevitVersion = "2024",
  [string]$RevitExe = "C:\Program Files\Autodesk\Revit 2024\Revit.exe",
  [string]$SourceModelPath = "C:\Program Files\Autodesk\Revit 2024\Samples\Snowdon Towers Sample Architectural.rvt",
  [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runtimeRoot = Join-Path $RunRoot "runtime"
New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null

$summaryPath = Join-Path $runtimeRoot "runtime_summary.csv"
$summaryHeader = "model_id,prompt_id,compile_success,execution_success,runtime_elapsed_ms,selected_element_ids,error,output_dir"
Set-Content -LiteralPath $summaryPath -Value $summaryHeader -Encoding UTF8

$addinDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
New-Item -ItemType Directory -Force -Path $addinDir | Out-Null
$addinPath = Join-Path $addinDir "Bibim.RevitAutoSmoke.addin"
$dllPath = Join-Path $repo "Bibim.RevitAutoSmoke\bin\Debug\net48\Bibim.RevitAutoSmoke.dll"
$addinXml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>BIBIM Revit AutoSmoke</Name>
    <Assembly>$dllPath</Assembly>
    <FullClassName>Bibim.RevitAutoSmoke.AutoSmokeApp</FullClassName>
    <AddInId>8D83B2F4-1B8B-4C5E-9B75-A1D3C3E7A024</AddInId>
    <VendorId>BIBIM</VendorId>
    <VendorDescription>BIBIM Revit runtime smoke test harness</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -LiteralPath $addinPath -Value $addinXml -Encoding UTF8

$smokeRoot = Join-Path $env:APPDATA "BIBIM\debug\revit-auto-smoke"
New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
$clicker = Join-Path $repo "testoutput\revit_modal_autoclick.ps1"

function Sanitize([string]$Value) {
  return ($Value -replace '[^A-Za-z0-9._-]', '_')
}

function Get-SelectionSpec([string]$PromptId) {
  $spec = [ordered]@{
    SelectElementCount = 0
    SelectionCategories = @()
    SelectionNameContains = $null
    SelectionTypeNameContains = $null
  }

  switch ($PromptId) {
    "guide_lv1_p1c_selected_element_parameters" {
      $spec.SelectElementCount = 1
      $spec.SelectionCategories = @("OST_Walls")
    }
    "guide_lv2_p2a_selected_wall_comments" {
      $spec.SelectElementCount = 1
      $spec.SelectionCategories = @("OST_Walls")
    }
    "guide_lv4_p4c_selected_curtain_wall_panels" {
      $spec.SelectElementCount = 1
      $spec.SelectionCategories = @("OST_Walls")
      $spec.SelectionTypeNameContains = "Curtain"
    }
    "guide_lv5_p5b_grid_intersections_place_columns" {
      $spec.SelectElementCount = 4
      $spec.SelectionCategories = @("OST_Grids")
    }
  }

  return $spec
}

function Stop-StartedRevit([System.Diagnostics.Process]$Process) {
  if ($null -eq $Process) { return }
  try {
    $p = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
    if ($p) { $p | Stop-Process -Force }
  } catch { }
}

try {
  $existingRevit = Get-Process -Name Revit -ErrorAction SilentlyContinue
  if ($existingRevit) {
    throw "Revit is already running. Close it before launching the automated runtime sweep."
  }

  $generationDirs = Get-ChildItem -LiteralPath (Join-Path $RunRoot "generation") -Directory
  $rows = foreach ($dir in $generationDirs) {
    $csv = Join-Path $dir.FullName "summary.csv"
    if (Test-Path -LiteralPath $csv) {
      Import-Csv -LiteralPath $csv | Where-Object { $_.compile_success -eq "True" }
    }
  }

  $index = 0
  $total = @($rows).Count
  foreach ($row in $rows) {
    $index++
    $modelId = $row.model_id
    $promptId = $row.prompt_id
    $debugDir = $row.debug_dir
    $generatedCodePath = Join-Path $debugDir "generated_code.cs"
    $safeModel = Sanitize $modelId
    $outputDir = Join-Path $runtimeRoot ("{0}__{1}" -f $safeModel, $promptId)
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

    Write-Output ("[{0}/{1}] {2} :: {3}" -f $index, $total, $modelId, $promptId)

    foreach ($name in @("batch_result.json", "result.json", "auto_smoke.log")) {
      $p = Join-Path $smokeRoot $name
      if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
    }

    $spec = Get-SelectionSpec $promptId
    $request = [pscustomobject]@{
      Id = ("{0}__{1}" -f $safeModel, $promptId)
      ModelId = $modelId
      PromptId = $promptId
      GeneratedCodePath = $generatedCodePath
      SourceModelPath = $SourceModelPath
      ViewName = ""
      CaptureScreenshot = $false
      ForceNewDocument = $true
      SelectElementCount = $spec.SelectElementCount
      SelectionCategories = $spec.SelectionCategories
      SelectionNameContains = $spec.SelectionNameContains
      SelectionTypeNameContains = $spec.SelectionTypeNameContains
      OutputDirectory = $outputDir
    }
    [pscustomobject]@{ Requests = @($request) } |
      ConvertTo-Json -Depth 8 |
      Set-Content -LiteralPath (Join-Path $smokeRoot "batch_requests.json") -Encoding UTF8

    $started = $null
    $runtimeSw = [System.Diagnostics.Stopwatch]::StartNew()
    $executionSuccess = $false
    $selectedIds = ""
    $errorText = ""
    try {
      $started = Start-Process -FilePath $RevitExe -PassThru -WindowStyle Minimized
      $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
      $resultPath = Join-Path $smokeRoot "batch_result.json"
      while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $clicker) {
          powershell -NoProfile -ExecutionPolicy Bypass -File $clicker | Out-Null
        }

        if (Test-Path -LiteralPath $resultPath) {
          try {
            $batch = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $result = @($batch.Results)[0]
            if ($result) {
              $executionSuccess = [bool]$result.ExecutionSuccess
              $selectedIds = (@($result.SelectedElementIds) -join "|")
              if ($result.ExecutionError) {
                $errorText = (($result.ExecutionError -split "`r?`n")[0] -replace '"', '""')
              }
              break
            }
          } catch {
            Start-Sleep -Seconds 1
          }
        }
        Start-Sleep -Seconds 2
      }

      if (-not (Test-Path -LiteralPath $resultPath)) {
        $errorText = "Runtime timeout after $TimeoutSeconds seconds"
      }
    }
    catch {
      $errorText = ($_.Exception.Message -replace '"', '""')
    }
    finally {
      $runtimeSw.Stop()
      Stop-StartedRevit $started
      Start-Sleep -Seconds 1
    }

    $line = '"{0}","{1}",True,{2},{3},"{4}","{5}","{6}"' -f `
      ($modelId -replace '"', '""'),
      ($promptId -replace '"', '""'),
      $executionSuccess,
      $runtimeSw.ElapsedMilliseconds,
      ($selectedIds -replace '"', '""'),
      $errorText,
      ($outputDir -replace '"', '""')
    Add-Content -LiteralPath $summaryPath -Value $line -Encoding UTF8
  }
}
finally {
  if (Test-Path -LiteralPath $addinPath) { Remove-Item -LiteralPath $addinPath -Force }
  $batchPath = Join-Path $smokeRoot "batch_requests.json"
  if (Test-Path -LiteralPath $batchPath) { Remove-Item -LiteralPath $batchPath -Force }
}
