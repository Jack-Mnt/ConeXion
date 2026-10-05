using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using ConeXion.Core.Models;
using ConeXion.Core.Services;

namespace ConeXion;

public partial class MainWindow : Window
{
    private const string AppVersion = "2.1.0";
    private readonly ConeXionRuntime _runtime = new(AppPaths.Production());
    private CatalogDocument? _catalog;
    private InstallationConfig? _installation;
    private bool _busy;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_,__) => await InitializeUiAsync();
    }

    private async Task InitializeUiAsync()
    {
        try
        {
            _installation = _runtime.Configuration.Load();
            if (_installation is null || !_installation.Provisioned || !_runtime.Credentials.Exists)
            { ShowUnprovisioned(); return; }

            await _runtime.InitializeAsync();
            StoreText.Text = _installation.StoreName;
            SetInputEnabled(false);
            var sync = await _runtime.Sync.RunAsync(SyncTrigger.Startup, AppVersion);
            SetConnectionBadge(sync.DataBaseAvailable);
            if (sync.PermanentFailure && !string.IsNullOrWhiteSpace(sync.Message))
                throw new InvalidDataException(sync.Message);
            if (!sync.DataBaseAvailable && !_runtime.Catalogs.Exists)
                throw new InvalidDataException("No hay conexión y esta PC aún no tiene un catálogo válido.");

            _catalog = _runtime.Catalogs.LoadCurrent();
            CatalogText.Text = $"Catálogo v{_catalog.CatalogVersion}";
            await RefreshStockStatusAsync();
            SetInputEnabled(true);
        }
        catch (Exception ex)
        {
            _runtime.Logs.Error("Inicio", ex);
            ShowBlockingError("No se pudo iniciar ConeXion", ex.Message);
        }
    }

    private async Task ProcessFileAsync(string path)
    {
        if (_catalog is null || _installation is null) return;
        _busy = true; SetInputEnabled(false); ShowProgress("Validando", Path.GetFileName(path), 1);
        try
        {
            var pre = await _runtime.Sync.RunAsync(SyncTrigger.BeforeNewImport, AppVersion);
            SetConnectionBadge(pre.DataBaseAvailable);
            if (pre.CatalogUpdated) { _catalog = _runtime.Catalogs.LoadCurrent(); CatalogText.Text = $"Catálogo v{_catalog.CatalogVersion}"; }

            // If Supabase is reachable but catalog sync failed, don't accept a new file.
            if (pre.DataBaseAvailable && !string.IsNullOrWhiteSpace(pre.Message))
                throw new InvalidDataException(pre.Message);

            var progress = new Progress<string>(m =>
            {
                var step = m.Contains("Comparando",StringComparison.OrdinalIgnoreCase)?2:m.Contains("Preparando",StringComparison.OrdinalIgnoreCase)?3:1;
                ShowProgress(m.TrimEnd('.'), Path.GetFileName(path), step);
            });
            var result = await Task.Run(() => _runtime.Processor.Process(path, _catalog, _installation.StoreName, progress));

            var usability = SnapshotUsabilityValidator.Validate(result.Snapshot, _catalog);
            if (!usability.IsUsable)
            {
                ShowUnusableSnapshot(usability);
                return;
            }

            if (await _runtime.LocalDb.SnapshotHashExistsAsync(_installation.StoreName, result.Snapshot.ExcelHash))
                throw new InvalidDataException("Este archivo ya fue importado.");

            // La ventana de 2 horas solo se evalúa después de confirmar que el archivo
            // es estructuralmente utilizable. Un Excel inválido nunca debe consumirla.
            await EnsureSnapshotWindowOpenAsync(pre);

            await _runtime.LocalDb.SaveSnapshotAsync(result.Snapshot, LocalSnapshotStatus.Pending);

            ShowProgress("Enviando", Path.GetFileName(path), 4);
            var sync = await _runtime.Sync.RunAsync(SyncTrigger.ImmediateAfterImport, AppVersion);
            SetConnectionBadge(sync.DataBaseAvailable);

            var pending = await _runtime.LocalDb.GetPendingAsync();
            var currentPending = pending.Any(x => x.SnapshotId == result.Snapshot.SnapshotId);
            var currentHistory = (await _runtime.LocalDb.GetHistoryAsync(20)).FirstOrDefault(x => x.SnapshotId == result.Snapshot.SnapshotId);
            if (currentHistory?.Status == LocalSnapshotStatus.Failed)
            {
                ShowWarning("El backend rechazó el inventario", currentHistory.LastError ?? "Revisa Historial para más detalle.");
                ShowReady();
            }
            else if (currentPending)
                ShowPendingConnectionFailure(result.Snapshot, sync.Message);
            else
            {
                await RefreshStockStatusAsync();
                await ShowSuccessAndCloseAsync(result.Snapshot);
            }
        }
        catch(Exception ex)
        {
            _runtime.Logs.Error("Procesamiento",ex);
            ShowWarning("No se pudo procesar el archivo",ex.Message);
            ShowReady();
        }
        finally { _busy=false; if(!_allowClose) SetInputEnabled(_catalog is not null && _installation?.Provisioned==true); }
    }

    private void SelectFileButton_Click(object sender,RoutedEventArgs e) => OpenFilePicker();

    private void ResultActionButton_Click(object sender, RoutedEventArgs e) => OpenFilePicker();

    private void OpenFilePicker()
    {
        if (_busy || _catalog is null) return;
        var d = new OpenFileDialog
        {
            Title = "Seleccionar inventario",
            Filter = "Archivos Excel (*.xlsx)|*.xlsx",
            CheckFileExists = true
        };
        if (d.ShowDialog(this) == true)
            _ = ProcessFileAsync(d.FileName);
    }

    private void DropZone_DragEnter(object sender,DragEventArgs e)
    {
        if(_busy||_catalog is null||!e.Data.GetDataPresent(DataFormats.FileDrop)){e.Effects=DragDropEffects.None;return;}
        var f=(string[])e.Data.GetData(DataFormats.FileDrop);
        var valid=f.Length==1&&string.Equals(Path.GetExtension(f[0]),".xlsx",StringComparison.OrdinalIgnoreCase);
        e.Effects=valid?DragDropEffects.Copy:DragDropEffects.None;
        if(valid)DropZone.Background=(Brush)FindResource("JmBrush.SurfaceAlt");
        e.Handled=true;
    }
    private void DropZone_DragLeave(object sender,DragEventArgs e)=>DropZone.Background=(Brush)FindResource("JmBrush.Surface");
    private void DropZone_Drop(object sender,DragEventArgs e)
    {
        DropZone.Background=(Brush)FindResource("JmBrush.Surface");
        if(_busy||_catalog is null||!e.Data.GetDataPresent(DataFormats.FileDrop))return;
        var f=(string[])e.Data.GetData(DataFormats.FileDrop);
        if(f.Length!=1||!string.Equals(Path.GetExtension(f[0]),".xlsx",StringComparison.OrdinalIgnoreCase)){ShowWarning("No se pudo procesar el archivo","Solo se admite un archivo .xlsx a la vez.");return;}
        _=ProcessFileAsync(f[0]);
    }

    private async Task RefreshStockStatusAsync()
    {
        var capturedText = await _runtime.LocalDb.GetStateAsync("last_snapshot_captured_at");
        DateTimeOffset? fallbackSynced = null;
        if (!DateTimeOffset.TryParse(capturedText, out _)) fallbackSynced = await _runtime.LocalDb.GetLatestSyncedCapturedAtAsync();
        var pending = await _runtime.LocalDb.GetPendingAsync();
        PendingText.Text = pending.Count == 0 ? "" : $"{pending.Count} inventario{(pending.Count == 1 ? "" : "s")} pendiente{(pending.Count == 1 ? "" : "s")} de sincronización";

        if (!DateTimeOffset.TryParse(capturedText, out var captured) && !fallbackSynced.HasValue)
        {
            StockStatusTitle.Text = "Aún no hay inventarios confirmados";
            StockStatusSubtitle.Text = pending.Count > 0 ? "Existe un inventario pendiente de sincronización." : "Carga un inventario para iniciar el stock de la sede.";
            StockStatusSubtitle.Foreground = (Brush)FindResource("JmBrush.TextSecondary");
            StockStatusSubtitle.FontWeight = FontWeights.Normal;
            return;
        }

        if (fallbackSynced.HasValue) captured = fallbackSynced.Value;
        var localCaptured = captured.ToLocalTime();
        var now = DateTimeOffset.Now;
        var elapsed = now - localCaptured;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var expires = localCaptured.AddHours(2);

        if (now < expires)
        {
            var remaining = expires - now;
            StockStatusTitle.Text = $"Stock vigente · actualizado hace {FormatElapsed(elapsed)}";
            StockStatusSubtitle.Text = $"Próxima actualización: {expires:HH:mm} · Disponible en {FormatRemaining(remaining)}";
            StockStatusSubtitle.Foreground = (Brush)FindResource("JmBrush.TextSecondary");
            StockStatusSubtitle.FontWeight = FontWeights.Normal;
        }
        else
        {
            StockStatusTitle.Text = $"Stock vencido · actualizado hace {FormatElapsed(elapsed)}";
            StockStatusSubtitle.Text = $"Debes cargar un nuevo inventario. Disponible desde {expires:HH:mm}.";
            StockStatusSubtitle.Foreground = (Brush)FindResource("JmBrush.Warning");
            StockStatusSubtitle.FontWeight = FontWeights.SemiBold;
        }
    }

    private async Task EnsureSnapshotWindowOpenAsync(SyncRunResult sync)
    {
        DateTimeOffset? reference = sync.LastSnapshotCapturedAt;
        if (!reference.HasValue)
        {
            var cached = await _runtime.LocalDb.GetStateAsync("last_snapshot_captured_at");
            if (DateTimeOffset.TryParse(cached, out var parsed)) reference = parsed;
            else reference = await _runtime.LocalDb.GetLatestSyncedCapturedAtAsync();
        }

        var latestPending = await _runtime.LocalDb.GetLatestPendingCapturedAtAsync();
        if (latestPending.HasValue && (!reference.HasValue || latestPending.Value > reference.Value))
            reference = latestPending;

        if (!reference.HasValue) return;

        var nextAllowed = reference.Value.AddHours(2);
        var now = sync.ServerNow ?? DateTimeOffset.UtcNow;
        if (now < nextAllowed)
        {
            var remaining = nextAllowed - now;
            throw new InvalidDataException($"El stock todavía está vigente. Podrás volver a actualizar desde {nextAllowed.ToLocalTime():HH:mm} (faltan {FormatRemaining(remaining)}). El intervalo mínimo entre snapshots es de 2 horas.");
        }
    }

    private static string FormatElapsed(TimeSpan x)=>x.TotalMinutes<1?"menos de 1 minuto":x.TotalHours<1?$"{(int)x.TotalMinutes} min":$"{(int)x.TotalHours} h {x.Minutes} min";
    private static string FormatRemaining(TimeSpan x)=>x.TotalMinutes<1?"menos de 1 minuto":x.TotalHours<1?$"{Math.Max(1,(int)Math.Ceiling(x.TotalMinutes))} min":$"{(int)x.TotalHours} h {x.Minutes} min";

    private void SetConnectionBadge(bool connected)
    {
        ConnectionBadge.Visibility=Visibility.Visible; ConnectionText.Text=connected?"● Conectado":"● Sin conexión";
        ConnectionText.Foreground=connected?(Brush)FindResource("JmBrush.Success"):(Brush)FindResource("JmBrush.Warning");
        ConnectionBadge.Background=connected?new SolidColorBrush(Color.FromRgb(236,253,243)):new SolidColorBrush(Color.FromRgb(255,247,237));
    }

    private void ShowUnprovisioned()
    {
        StoreText.Text="";CatalogText.Text="";ConnectionBadge.Visibility=Visibility.Collapsed;
        StockStatusTitle.Text="Esta computadora no está configurada.";StockStatusSubtitle.Text="";PendingText.Text="";
        ReadyPanel.Visibility=Visibility.Collapsed;ProgressPanel.Visibility=Visibility.Collapsed;ResultPanel.Visibility=Visibility.Collapsed;
        DropZone.BorderBrush=Brushes.Transparent;SetInputEnabled(false);
    }

    private void ShowProgress(string title,string file,int step)
    {
        ResetResultDetails();
        ReadyPanel.Visibility=Visibility.Collapsed;ResultPanel.Visibility=Visibility.Collapsed;ProgressPanel.Visibility=Visibility.Visible;
        ProgressTitle.Text=title;ProgressFile.Text=file;
        var all=new[]{Step1,Step2,Step3,Step4,Step5};
        for(var i=0;i<all.Length;i++){all[i].Foreground=i+1<=step?(Brush)FindResource("JmBrush.Primary"):(Brush)FindResource("JmBrush.TextDisabled");all[i].FontWeight=i+1==step?FontWeights.SemiBold:FontWeights.Normal;}
    }

    private void ShowReady()
    {
        ResetResultDetails();
        ProgressPanel.Visibility=Visibility.Collapsed;ResultPanel.Visibility=Visibility.Collapsed;ReadyPanel.Visibility=Visibility.Visible;
    }

    private void ShowUnusableSnapshot(SnapshotUsabilityResult usability)
    {
        ProgressPanel.Visibility=Visibility.Collapsed;
        ReadyPanel.Visibility=Visibility.Collapsed;
        ResultPanel.Visibility=Visibility.Visible;
        ResultTitle.Text="No se puede actualizar el inventario";
        ResultTitle.Foreground=(Brush)FindResource("JmBrush.Warning");
        ResultSubtitle.Text="El archivo contiene errores que impedirían iniciar un conteo en SOLOG. Corrígelos antes de continuar.";
        ResultSummary.Text="";
        CountdownText.Text="";

        ResultDetailsPanel.Children.Clear();
        foreach (var blocker in usability.Blockers)
            ResultDetailsPanel.Children.Add(BuildBlockingCard(blocker));

        ResultDetailsScroll.Visibility=Visibility.Visible;
        ResultActionButton.Visibility=Visibility.Visible;
        ResultActionButton.IsEnabled=true;
    }

    private Border BuildBlockingCard(SnapshotUsabilityBlocker blocker)
    {
        var content = new StackPanel();

        var product = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("JmBrush.TextPrimary"),
            FontSize = 15
        };
        product.Inlines.Add(new Run($"Código {blocker.CInterno} — ") { FontWeight = FontWeights.SemiBold });
        product.Inlines.Add(new Run(blocker.Producto));
        content.Children.Add(product);

        content.Children.Add(new TextBlock
        {
            Text = BlockerTitle(blocker.Motivo),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("JmBrush.Warning"),
            Margin = new Thickness(0, 6, 0, 8)
        });

        if (blocker.Observations.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Encontrado en:",
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("JmBrush.TextPrimary"),
                Margin = new Thickness(0, 0, 0, 4)
            });

            foreach (var observation in blocker.Observations)
            {
                var row = observation.Row.HasValue ? $"Fila {observation.Row.Value}" : "Fila no identificada";
                var value = string.IsNullOrWhiteSpace(observation.Value) ? "(vacío)" : observation.Value;
                var detail = blocker.Motivo == "codigo_interno_duplicado"
                    ? $"• {row} — stock {value}"
                    : blocker.Motivo == "stock_invalido"
                        ? $"• {row} — valor encontrado: {value}"
                        : $"• {row} — {value}";

                content.Children.Add(new TextBlock
                {
                    Text = detail,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("JmBrush.TextSecondary"),
                    Margin = new Thickness(0, 0, 0, 3)
                });
            }
        }

        content.Children.Add(new TextBlock
        {
            Text = "Cómo solucionarlo:",
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("JmBrush.TextPrimary"),
            Margin = new Thickness(0, 10, 0, 4)
        });

        foreach (var step in BlockerResolutionSteps(blocker))
        {
            content.Children.Add(new TextBlock
            {
                Text = step,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("JmBrush.TextSecondary"),
                Margin = new Thickness(0, 0, 0, 3)
            });
        }

        return new Border
        {
            Background = (Brush)FindResource("JmBrush.SurfaceAlt"),
            BorderBrush = (Brush)FindResource("JmBrush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content
        };
    }

    private static string BlockerTitle(string reason)
        => reason switch
        {
            "codigo_interno_duplicado" => "Código interno duplicado",
            "stock_invalido" => "Stock inválido",
            _ => "Stock no resoluble"
        };

    private static IReadOnlyList<string> BlockerResolutionSteps(SnapshotUsabilityBlocker blocker)
        => blocker.Motivo switch
        {
            "codigo_interno_duplicado" =>
            [
                "1. Abre Tumisoft.",
                $"2. Busca el código interno {blocker.CInterno}.",
                "3. Corrige el código interno de los códigos duplicados.",
                "4. Guarda los cambios y descarga el inventario nuevamente.",
                "5. Carga el nuevo Excel en ConeXion."
            ],
            "stock_invalido" =>
            [
                "1. Abre el archivo de inventario.",
                blocker.Observations.FirstOrDefault()?.Row is int row
                    ? $"2. Ve a la fila {row}."
                    : "2. Ve a la fila indicada.",
                "3. Corrige el valor de Stock para que sea un NÚMERO ENTERO VÁLIDO.",
                "4. No elimines el producto ni cambies su código interno.",
                "5. Guarda el archivo y vuelve a cargarlo en ConeXion."
            ],
            _ =>
            [
                "1. Revisa el producto indicado en el archivo de inventario.",
                "2. Corrige el dato que impide determinar un stock válido.",
                "3. Guarda el archivo y vuelve a cargarlo en ConeXion."
            ]
        };

    private void ResetResultDetails()
    {
        ResultDetailsPanel.Children.Clear();
        ResultDetailsScroll.Visibility=Visibility.Collapsed;
        ResultActionButton.Visibility=Visibility.Collapsed;
        ResultActionButton.IsEnabled=false;
    }

    private void ShowPendingConnectionFailure(SnapshotDocument s,string? detail)
    {
        ResetResultDetails();
        ProgressPanel.Visibility=Visibility.Collapsed;ReadyPanel.Visibility=Visibility.Collapsed;ResultPanel.Visibility=Visibility.Visible;
        ResultTitle.Text="No se ha podido cargar el inventario por fallo en la conexión.";ResultTitle.Foreground=(Brush)FindResource("JmBrush.Warning");
        ResultSubtitle.Text=string.IsNullOrWhiteSpace(detail)?"El inventario quedó guardado localmente.":detail;ResultSummary.Text=BuildSummary(s);CountdownText.Text="";
    }
    private async Task ShowSuccessAndCloseAsync(SnapshotDocument s)
    {
        ResetResultDetails();
        ProgressPanel.Visibility=Visibility.Collapsed;ReadyPanel.Visibility=Visibility.Collapsed;ResultPanel.Visibility=Visibility.Visible;
        ResultTitle.Foreground=(Brush)FindResource("JmBrush.Success");ResultTitle.Text="Exportación correcta, se autodestruirá en 5 segundos...";
        ResultSubtitle.Text=s.Incidencias.Count==0?"":"Se detectaron incidencias y fueron registradas para revisión.";ResultSummary.Text=BuildSummary(s);
        for(int i=5;i>=1;i--){CountdownText.Text=i.ToString();await Task.Delay(1000);}
        _allowClose=true;Close();
    }
    private static string BuildSummary(SnapshotDocument s)=>$"SKU Excel: {s.Resumen.SkuTotalExcel:N0}   ·   Catálogo: {s.Resumen.SkuCatalogo:N0}   ·   Excluidos: {s.Resumen.SkuExcluidos:N0}\nStock ≠ 0: {s.Resumen.SkuStockNoCero:N0}   ·   Stock cero: {s.Resumen.SkuStockCero:N0}   ·   Sin observación válida: {s.Resumen.SkuEliminados:N0}   ·   Incidencias: {s.Resumen.IncidenciasTotal:N0}";

    private async void History_Click(object sender,RoutedEventArgs e)
    {
        SidePanelTitle.Text="Historial";SidePanelContent.Children.Clear();
        var rows=await _runtime.LocalDb.GetHistoryAsync(10);
        foreach(var r in rows) AddSideCard($"{r.CreatedAt:dd/MM HH:mm}\n{r.SourceFileName}\n{r.Status} · {r.IncidentCount} incidencias",(Brush)FindResource("JmBrush.TextPrimary"));
        if(rows.Count==0)AddEmptySideMessage("No hay registros.");OpenSidePanel();
    }
    private async void Incidents_Click(object sender,RoutedEventArgs e)
    {
        SidePanelTitle.Text="Incidencias";SidePanelContent.Children.Clear();
        foreach(var r in await _runtime.LocalDb.GetHistoryAsync(10))
        {
            var s=SnapshotExporter.Deserialize(r.PayloadJson);
            foreach(var i in s.Incidencias.Take(30))
                AddSideCard($"{i.Tipo}\nC. interno: {i.CInterno?.ToString() ?? i.CInternoOriginal ?? "—"}\n{JsonSerializer.Serialize(i.Datos)}",(Brush)FindResource("JmBrush.TextPrimary"));
        }
        if(SidePanelContent.Children.Count==0)AddEmptySideMessage("No hay incidencias recientes.");OpenSidePanel();
    }
    private void OpenSidePanel(){SidePanel.Visibility=Visibility.Visible;SidePanelColumn.Width=new GridLength(378);}
    private void CloseSidePanel_Click(object sender,RoutedEventArgs e){SidePanel.Visibility=Visibility.Collapsed;SidePanelColumn.Width=new GridLength(0);}
    private void AddEmptySideMessage(string m)=>SidePanelContent.Children.Add(new TextBlock{Text=m,Foreground=(Brush)FindResource("JmBrush.TextSecondary"),TextWrapping=TextWrapping.Wrap});
    private void AddSideCard(string text,Brush fg)=>SidePanelContent.Children.Add(new Border{Background=(Brush)FindResource("JmBrush.SurfaceAlt"),CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Margin=new Thickness(0,0,0,10),Child=new TextBlock{Text=text,Foreground=fg,TextWrapping=TextWrapping.Wrap}});
    private void SetInputEnabled(bool enabled){SelectFileButton.IsEnabled=enabled;DropZone.AllowDrop=enabled;DropZone.BorderBrush=enabled?(Brush)FindResource("JmBrush.Primary"):(Brush)FindResource("JmBrush.Border");WaitText.Text=enabled?"Solo se admiten archivos .xlsx":"Carga temporalmente deshabilitada · Esperando...";}
    private void ShowWarning(string title,string message)=>MessageBox.Show(this,message,title,MessageBoxButton.OK,MessageBoxImage.Warning);
    private void ShowBlockingError(string title,string message){ShowWarning(title,message);SetInputEnabled(false);}
    private void Window_Closing(object? sender,CancelEventArgs e){if(!_busy||_allowClose)return;var r=MessageBox.Show(this,"Hay una operación en curso.\n¿Deseas cerrar ConeXion?","ConeXion",MessageBoxButton.YesNo,MessageBoxImage.Warning);if(r!=MessageBoxResult.Yes)e.Cancel=true;}
}
