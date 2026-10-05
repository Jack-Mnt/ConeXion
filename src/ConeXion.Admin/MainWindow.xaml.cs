using System.IO;
using System.IO.Compression;
using System.Windows;
using Microsoft.Win32;
using ConeXion.Core.Models;
using ConeXion.Core.Services;

namespace ConeXion.Admin;

public partial class MainWindow : Window
{
    private const string AppVersion = "2.0.2";
    private readonly ConeXionRuntime _runtime = new(AppPaths.Production());
    private ProvisioningTxt? _pendingTxt;

    public MainWindow(){InitializeComponent();Loaded+=async(_,__)=>{_runtime.Permissions.PrepareForStandardUsers();await _runtime.InitializeAsync();RefreshState();};}

    private void RefreshState()
    {
        var c=_runtime.Configuration.Load();var ok=c?.Provisioned==true&&_runtime.Credentials.Exists;
        UnprovisionedPanel.Visibility=ok?Visibility.Collapsed:Visibility.Visible;ProvisionedPanel.Visibility=ok?Visibility.Visible:Visibility.Collapsed;
        if(!ok||c is null)return;
        InstallationText.Text=$"Sede: {c.StoreName}\nInstallation ID: {c.InstallationId}\nEstado: Aprovisionada";
        try{var cat=_runtime.Catalogs.LoadCurrent();CatalogAdminText.Text=$"Versión: v{cat.CatalogVersion} · schema {cat.SchemaVersion}\nProductos: {cat.Productos.Count:N0}\nExcluidos: {cat.Excluidos.Count:N0}\nEstado: Correcto";}
        catch(Exception ex){CatalogAdminText.Text=$"Estado: Error\n{ex.Message}";}
    }

    private void LoadTxt_Click(object sender,RoutedEventArgs e)
    {
        var d=new OpenFileDialog{Title="Seleccionar archivo de configuración",Filter="Archivo de texto (*.txt)|*.txt",CheckFileExists=true};
        if(d.ShowDialog(this)!=true)return;
        try{_pendingTxt=ParseTxt(File.ReadAllLines(d.FileName));PreviewText.Text=$"Sede: {_pendingTxt.Sede}\nInstallation ID: {_pendingTxt.InstallationId}\nInstallation Secret: ********";PreviewCard.Visibility=Visibility.Visible;ProvisionMessage.Text="";}
        catch(Exception ex){_pendingTxt=null;MessageBox.Show(this,ex.Message,"No se pudo procesar el archivo TXT",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private async void ApplyConfig_Click(object sender,RoutedEventArgs e)=>await ApplyPendingConfigurationAsync();

    private async Task ApplyPendingConfigurationAsync()
    {
        if(_pendingTxt is null)return;
        try
        {
            ProvisionMessage.Text="Validando credenciales con Supabase...";
            _runtime.Permissions.PrepareForStandardUsers();
            var creds=new InstallationCredentials(_pendingTxt.InstallationId,_pendingTxt.InstallationSecret);
            var state=await _runtime.DataBase.GetStateAsync(creds,AppVersion,null,null);
            if(!state.Ok)throw new InvalidDataException(state.Codigo);
            if(state.CatalogSchemaVersion!=CatalogService.SupportedSchemaVersion)throw new InvalidDataException($"Schema de catálogo no compatible: {state.CatalogSchemaVersion}. ConeXion requiere schema {CatalogService.SupportedSchemaVersion}.");
            if(!string.Equals(state.StoreName.Trim(),_pendingTxt.Sede.Trim(),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"El TXT indica {_pendingTxt.Sede}, pero Supabase devolvió {state.StoreName}.");

            var download=await _runtime.DataBase.DownloadCatalogAsync(creds,AppVersion,null,null);
            if(!download.Accepted||download.Package is null)throw new InvalidDataException(download.Error??"No se pudo descargar el catálogo.");
            _runtime.Catalogs.InstallPackageAtomically(download.Package,download.Sha256,download.SizeBytes,download.Version,download.SchemaVersion);
            try
            {
                if(!await _runtime.DataBase.ConfirmCatalogAsync(creds,AppVersion,download.Version,download.Sha256))
                    throw new InvalidDataException("No se pudo confirmar el catálogo.");
            }
            catch
            {
                _runtime.Catalogs.RollbackUnconfirmedInstall();
                throw;
            }

            _runtime.Credentials.SaveSecret(_pendingTxt.InstallationSecret);
            _runtime.Configuration.Save(new InstallationConfig{
                InstallationId=_pendingTxt.InstallationId,StoreId=state.StoreId,StoreName=state.StoreName,
                CatalogVersion=download.Version,CatalogSha256=download.Sha256,Provisioned=true
            });
            ProvisionMessage.Foreground=(System.Windows.Media.Brush)FindResource("JmBrush.Success");
            ProvisionMessage.Text="Configuración aplicada correctamente.";_pendingTxt=null;PreviewCard.Visibility=Visibility.Collapsed;RefreshState();
        }
        catch(Exception ex){ProvisionMessage.Foreground=(System.Windows.Media.Brush)FindResource("JmBrush.Error");ProvisionMessage.Text=ex.Message;}
    }

    private async void RepairCatalog_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var cfg=_runtime.Configuration.Load()??throw new InvalidDataException("No existe configuración.");
            var creds=_runtime.Credentials.Load(cfg);
            var dl=await _runtime.DataBase.DownloadCatalogAsync(creds,AppVersion,0,"");
            if(!dl.Accepted||dl.Package is null)throw new InvalidDataException(dl.Error??"No se pudo descargar el catálogo.");
            _runtime.Catalogs.InstallPackageAtomically(dl.Package,dl.Sha256,dl.SizeBytes,dl.Version,dl.SchemaVersion);
            try
            {
                if(!await _runtime.DataBase.ConfirmCatalogAsync(creds,AppVersion,dl.Version,dl.Sha256))
                    throw new InvalidDataException("Supabase no confirmó el catálogo reparado.");
            }
            catch
            {
                _runtime.Catalogs.RollbackUnconfirmedInstall();
                throw;
            }
            cfg.CatalogVersion=dl.Version;cfg.CatalogSha256=dl.Sha256;_runtime.Configuration.Save(cfg);RefreshState();
            MessageBox.Show(this,"Catálogo reparado correctamente.","ConeXion Admin",MessageBoxButton.OK,MessageBoxImage.Information);
        }catch(Exception ex){MessageBox.Show(this,ex.Message,"No se pudo reparar el catálogo",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }

    private async void Diagnostic_Click(object sender,RoutedEventArgs e)
    {
        var items=await _runtime.Diagnostics.RunAsync();
        DiagnosticResult.Text=string.Join(Environment.NewLine,items.Select(x=>$"{(x.Success?"✓":"⚠")} {x.Name}: {x.Detail}"));
    }
    private async void ExportDiagnostic_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var items=await _runtime.Diagnostics.RunAsync();var temp=Path.Combine(Path.GetTempPath(),$"ConeXion_Diagnostico_{DateTime.Now:yyyyMMdd_HHmmss}");Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(temp,"diagnostico.txt"),string.Join(Environment.NewLine,items.Select(x=>$"{x.Name}: {(x.Success?"Correcto":"Error")} - {x.Detail}")));
            if(Directory.Exists(_runtime.Paths.Logs))foreach(var f in Directory.GetFiles(_runtime.Paths.Logs,"*.log").TakeLast(10))File.Copy(f,Path.Combine(temp,Path.GetFileName(f)),true);
            var zip=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),$"ConeXion_Diagnostico_{DateTime.Now:yyyyMMdd_HHmmss}.zip");ZipFile.CreateFromDirectory(temp,zip);Directory.Delete(temp,true);
            MessageBox.Show(this,$"Diagnóstico exportado en:\n{zip}","ConeXion Admin",MessageBoxButton.OK,MessageBoxImage.Information);
        }catch(Exception ex){MessageBox.Show(this,ex.Message,"No se pudo exportar el diagnóstico",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }

    private void Reprovision_Click(object sender,RoutedEventArgs e)
    {
        if(MessageBox.Show(this,"Esta acción eliminará la configuración local actual.\n¿Deseas continuar?","Reprovisionar",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        _runtime.Credentials.Clear();_runtime.Configuration.Clear();RefreshState();
    }

    private static ProvisioningTxt ParseTxt(IEnumerable<string> lines)
    {
        var f=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var raw in lines){if(string.IsNullOrWhiteSpace(raw))continue;var i=raw.IndexOf(':');if(i>0)f[raw[..i].Trim()]=raw[(i+1)..].Trim();}
        string Req(string k)=>f.TryGetValue(k,out var v)&&!string.IsNullOrWhiteSpace(v)?v:throw new InvalidDataException($"Falta el campo obligatorio: {k}.");
        return new(Req("Sede"),Req("Installation ID"),Req("Installation Secret"));
    }
    private sealed record ProvisioningTxt(string Sede,string InstallationId,string InstallationSecret);
}
