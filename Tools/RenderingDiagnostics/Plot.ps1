param(
    [Parameter(Mandatory = $true)][string]$Summary,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$ComparisonSummary
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms.DataVisualization
$captureRows = Import-Csv -LiteralPath $Summary
$captureDataSets = @([pscustomobject]@{ Name = 'reference'; Rows = $captureRows })
if ($ComparisonSummary) {
    $captureDataSets += [pscustomobject]@{ Name = 'intervention'; Rows = (Import-Csv -LiteralPath $ComparisonSummary) }
}
$captureChart = New-Object System.Windows.Forms.DataVisualization.Charting.Chart
try {
    $captureChart.Width = 1500
    $captureChart.Height = 900
    $captureChart.BackColor = [System.Drawing.Color]::White
    $captureChart.Titles.Add('Percurso A-B: contadores globais por pose (medianas)') | Out-Null
    $capturePaths = @('combined', 'position', 'rotation')
    $captureMetrics = @('batches', 'setpass')
    for ($captureColumn = 0; $captureColumn -lt 3; $captureColumn++) {
        for ($captureRow = 0; $captureRow -lt 2; $captureRow++) {
            $capturePath = $capturePaths[$captureColumn]
            $captureMetric = $captureMetrics[$captureRow]
            $captureAreaName = "$capturePath-$captureMetric"
            $captureArea = New-Object System.Windows.Forms.DataVisualization.Charting.ChartArea $captureAreaName
            $captureArea.Position = New-Object System.Windows.Forms.DataVisualization.Charting.ElementPosition (2 + $captureColumn * 33), (8 + $captureRow * 44), 32, 42
            $captureArea.AxisX.Title = "$capturePath : t (A=0, B=1)"
            $captureArea.AxisX.Minimum = 0
            $captureArea.AxisX.Maximum = 1
            $captureArea.AxisY.Title = $captureMetric
            $captureArea.AxisY.IsStartedFromZero = $false
            $captureArea.AxisX.MajorGrid.LineColor = [System.Drawing.Color]::LightGray
            $captureArea.AxisY.MajorGrid.LineColor = [System.Drawing.Color]::LightGray
            $captureChart.ChartAreas.Add($captureArea)
            foreach ($captureDataSet in $captureDataSets) {
              foreach ($captureDirection in @('AB', 'BA')) {
                $captureSeries = New-Object System.Windows.Forms.DataVisualization.Charting.Series "$captureAreaName-$($captureDataSet.Name)-$captureDirection"
                $captureSeries.ChartArea = $captureAreaName
                $captureSeries.ChartType = [System.Windows.Forms.DataVisualization.Charting.SeriesChartType]::Line
                $captureSeries.BorderWidth = 2
                $captureSeries.MarkerStyle = [System.Windows.Forms.DataVisualization.Charting.MarkerStyle]::Circle
                $captureSeries.MarkerSize = 5
                $captureBlue = if ($ComparisonSummary) { $captureDataSet.Name -eq 'reference' } else { $captureDirection -eq 'AB' }
                $captureSeries.Color = if ($captureBlue) { [System.Drawing.Color]::RoyalBlue } else { [System.Drawing.Color]::DarkOrange }
                if ($ComparisonSummary -and $captureDirection -eq 'BA') { $captureSeries.BorderDashStyle = [System.Windows.Forms.DataVisualization.Charting.ChartDashStyle]::Dash }
                foreach ($capturePoint in ($captureDataSet.Rows | Where-Object { $_.path -eq $capturePath -and $_.direction -eq $captureDirection } | Sort-Object { [double]::Parse($_.t, [System.Globalization.CultureInfo]::InvariantCulture) })) {
                    $captureValue = $capturePoint."${captureMetric}_median"
                    if ($captureValue -ne '') {
                        $captureSeries.Points.AddXY([double]::Parse($capturePoint.t, [System.Globalization.CultureInfo]::InvariantCulture), [double]::Parse($captureValue, [System.Globalization.CultureInfo]::InvariantCulture)) | Out-Null
                    }
                }
                $captureChart.Series.Add($captureSeries)
              }
            }
        }
    }
    $captureLegend = if ($ComparisonSummary) { 'Azul: referência | Laranja: intervenção | Contínuo: A para B | Tracejado: B para A' } else { 'Azul: A para B | Laranja: B para A. Degraus indicam intervalos a investigar; não identificam células.' }
    $captureChart.Titles.Add($captureLegend) | Out-Null
    $captureOutputPath = [System.IO.Path]::GetFullPath($Output)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($captureOutputPath)) | Out-Null
    $captureChart.SaveImage($captureOutputPath, [System.Windows.Forms.DataVisualization.Charting.ChartImageFormat]::Png)
    Write-Output $captureOutputPath
} finally {
    $captureChart.Dispose()
}
