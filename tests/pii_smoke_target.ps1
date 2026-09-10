Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$window = New-Object System.Windows.Window
$window.Width = 720
$window.Height = 420
$window.WindowStartupLocation = 'CenterScreen'
$window.Title = 'HelpSys visible-data redaction smoke'
$window.Background = [System.Windows.Media.Brushes]::White

$panel = New-Object System.Windows.Controls.StackPanel
$panel.Margin = '36'

$heading = New-Object System.Windows.Controls.TextBlock
$heading.FontSize = 24
$heading.Margin = '0,0,0,24'
$heading.Text = 'Visible data redaction test surface'
$panel.Children.Add($heading) | Out-Null

$email = New-Object System.Windows.Controls.TextBlock
$email.Name = 'VisibleEmailText'
$email.FontSize = 22
$email.Margin = '0,0,0,16'
$email.Text = 'alice@example.com'
$panel.Children.Add($email) | Out-Null

$phone = New-Object System.Windows.Controls.TextBlock
$phone.Name = 'VisiblePhoneText'
$phone.FontSize = 22
$phone.Margin = '0,0,0,16'
$phone.Text = '090-1234-5678'
$panel.Children.Add($phone) | Out-Null

$postal = New-Object System.Windows.Controls.TextBlock
$postal.Name = 'VisiblePostalText'
$postal.FontSize = 22
$postal.Margin = '0,0,0,28'
$postal.Text = '〒123-4567'
$panel.Children.Add($postal) | Out-Null

$button = New-Object System.Windows.Controls.Button
$button.Name = 'SmokeButton'
$button.Content = 'Open test target'
$button.Width = 220
$button.Height = 44
$button.HorizontalAlignment = 'Left'
$panel.Children.Add($button) | Out-Null

$note = New-Object System.Windows.Controls.TextBlock
$note.FontSize = 12
$note.Margin = '0,22,0,0'
$note.Text = 'All values on this screen are synthetic smoke-test data.'
$panel.Children.Add($note) | Out-Null

$window.Content = $panel
$window.Add_ContentRendered({ $window.Activate() })
$window.ShowDialog() | Out-Null
