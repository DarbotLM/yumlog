BeforeAll {
    . "$PSScriptRoot\Run-FFmpeg.ps1"
    . "$PSScriptRoot\Record-Screen.ps1"
    . "$PSScriptRoot\Capture-Screens.ps1"
}

Describe "yumlog Record-Screen" {
    It "passes the requested recording contract to FFmpeg" {
        Mock Run-FFmpeg

        Record-Screen -Fps 12 -DurationSec 7 -OutFile "recording.mp4"

        Should -Invoke Run-FFmpeg -Times 1 -Exactly -ParameterFilter {
            $Arguments -contains '-framerate' -and
            $Arguments -contains '12' -and
            $Arguments -contains '-t' -and
            $Arguments -contains '7' -and
            $Arguments[-1] -eq 'recording.mp4'
        }
    }

    It "rejects invalid timing" {
        { Record-Screen -Fps 0 -DurationSec 1 } | Should -Throw
        { Record-Screen -Fps 1 -DurationSec 0 } | Should -Throw
    }
}

Describe "yumlog Capture-Screens" {
    It "rejects invalid timing before touching the display" {
        { Capture-Screens -Fps 0 -DurationSec 1 } | Should -Throw
        { Capture-Screens -Fps 1 -DurationSec 0 } | Should -Throw
    }
}

Describe "yumlog FFmpeg wrapper" {
    It "surfaces a nonzero FFmpeg exit code" {
        Mock Start-Process { [pscustomobject]@{ ExitCode = 3 } }
        Mock Get-Content -ParameterFilter { $Path -eq 'ffmpeg_error.log' } { @('encoder failed') }

        { Run-FFmpeg -Arguments @('-version') } | Should -Throw '*exit code 3*'
    }
}
