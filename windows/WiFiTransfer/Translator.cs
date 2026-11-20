using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WiFiTransfer;

/// <summary>
/// Idioma do app. O texto original é em português; em inglês, cada texto da tela
/// passa por esta tabela (frases inteiras primeiro, depois expressões menores).
/// </summary>
public static class Translator
{
    public static bool English { get; private set; }

    private static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        // ---- frases inteiras
        ["Qualquer aparelho com o LAN Transfer na rede vê e baixa estes arquivos. Eles são enviados de onde estão, sem cópia."] =
            "Any device with LAN Transfer on the network can see and download these files. They are sent from where they are, without copies.",
        ["Compartilhamento pausado: ninguém vê seus arquivos até você ligar de novo."] = "Sharing paused: nobody sees your files until you turn it back on.",
        ["Arraste arquivos ou pastas para cá"] = "Drag files or folders here",
        ["ou clique com o botão direito no Explorer > Disponibilizar no LAN Transfer"] = "or right-click in Explorer > Disponibilizar no LAN Transfer",
        ["Arquivos de outros aparelhos com o app. Você também pode enviar direto para eles."] = "Files from other devices with the app. You can also send directly to them.",
        ["Buscar arquivos (aqui e nos outros aparelhos)…"] = "Search files (here and on other devices)…",
        ["Procurando aparelhos na rede…"] = "Looking for devices on the network…",
        ["Abra o LAN Transfer no celular ou em outro PC."] = "Open LAN Transfer on your phone or another PC.",
        ["Nenhum arquivo disponível neste aparelho."] = "No files available on this device.",
        ["Os arquivos que você baixar, receber ou enviar aparecem aqui."] = "Files you download, receive or send show up here.",
        ["Usar sem instalar o app"] = "Use without installing the app",
        ["Aponte a câmera do celular para o código. Abre uma página no navegador para baixar os arquivos de \"Meus arquivos\" ou enviar arquivos para este PC. O celular precisa estar na mesma rede."] =
            "Point the phone camera at the code. It opens a browser page to download the files in \"My files\" or send files to this PC. The phone must be on the same network.",
        ["Se o Chrome não abrir a página, use outro navegador (ex.: Samsung Internet)."] = "If Chrome doesn't open the page, use another browser (e.g. Samsung Internet).",
        ["O PIN aparece nas configurações do LAN Transfer do outro aparelho."] = "The PIN is shown in LAN Transfer settings on the other device.",
        ["Quem quiser ver ou baixar seus arquivos precisa digitar o PIN abaixo."] = "Anyone who wants to see or download your files must type the PIN below.",
        ["Só os aparelhos marcados abaixo veem seus arquivos e podem enviar para você. Navegadores (QR code) continuam pedindo sua confirmação para enviar e não veem os arquivos."] =
            "Only the devices checked below see your files and can send to you. Browsers (QR code) still need your confirmation to send and can't see the files.",
        ["Envios, sincronizações e textos de aparelhos marcados acima entram sem perguntar."] = "Transfers, syncs and texts from the devices checked above come in without asking.",
        ["Usa conexão criptografada com aparelhos que têm o LAN Transfer 5 ou mais novo. O navegador (QR code) continua sem criptografia."] =
            "Uses an encrypted connection with devices running LAN Transfer 5 or newer. The browser (QR code) stays unencrypted.",
        ["Arquivos novos ou alterados nestas pastas são enviados sozinhos para o aparelho quando ele estiver na rede. Para criar, use Enviar > Sincronizar uma pasta em um aparelho da lista."] =
            "New or changed files in these folders are sent automatically to the device when it's on the network. To create one, use Send > Sync a folder on a device in the list.",
        ["O X esconde a janela e o app continua compartilhando. Para sair, use o ícone perto do relógio."] =
            "The X hides the window and the app keeps sharing. To quit, use the icon near the clock.",
        ["Já vem com o que está na sua área de transferência. No outro aparelho o texto fica pronto para colar."] =
            "It starts with what's in your clipboard. On the other device the text is ready to paste.",
        ["Enviar arquivos, pasta ou texto direto para este aparelho. Você também pode arrastar arquivos para cá."] =
            "Send files, a folder or text directly to this device. You can also drag files here.",
        ["Para quem não tem o app: lê o código com a câmera e usa pelo navegador"] = "For people without the app: scan the code with the camera and use the browser",
        ["Disponibilizar uma pasta inteira, com as subpastas"] = "Share a whole folder, with its subfolders",
        ["Parar de compartilhar (não apaga o arquivo)"] = "Stop sharing (doesn't delete the file)",
        ["Vale depois de abrir o app de novo."] = "Applies after reopening the app.",
        ["Nenhuma pasta sincronizada."] = "No synced folders.",
        ["Nenhum aparelho visto ainda."] = "No devices seen yet.",
        ["Nada por aqui ainda."] = "Nothing here yet.",
        ["Aceitar sozinho de aparelhos de confiança"] = "Auto-accept from trusted devices",
        ["Criptografar entre aparelhos (HTTPS)"] = "Encrypt between devices (HTTPS)",
        ["Só aparelhos de confiança"] = "Trusted devices only",
        ["Iniciar com o Windows (na bandeja)"] = "Start with Windows (in the tray)",
        ["Fechar para a bandeja"] = "Close to tray",
        ["Avisar quando alguém baixar meus arquivos"] = "Notify me when someone downloads my files",
        ["Igual ao Windows"] = "Same as Windows",
        ["O LAN Transfer continua aberto"] = "LAN Transfer is still running",
        ["Seus arquivos seguem disponíveis. Clique no ícone perto do relógio para abrir ou sair."] = "Your files are still available. Click the icon near the clock to open or quit.",
        ["Confiar neste aparelho"] = "Trust this device",
        ["Ver sem baixar"] = "View without downloading",
        ["Continuar de onde parou"] = "Resume where it stopped",
        ["Mostrar na pasta"] = "Show in folder",
        ["Abrir / copiar"] = "Open / copy",
        ["Limpar histórico"] = "Clear history",
        ["Limpar lista"] = "Clear list",
        ["Abrir recebidos"] = "Open received",
        ["Mudar pasta"] = "Change folder",
        ["Meus arquivos"] = "My files",
        ["Disponíveis na rede"] = "Available on the network",
        ["Transferências"] = "Transfers",
        ["Histórico"] = "History",
        ["Configurações"] = "Settings",
        ["Baixar selecionados"] = "Download selected",
        ["Digitar PIN"] = "Enter PIN",
        ["Exigir PIN"] = "Require PIN",
        ["Gerar outro"] = "New PIN",
        ["Tema"] = "Theme", ["Idioma"] = "Language", ["Claro"] = "Light", ["Escuro"] = "Dark",
        ["Visível"] = "Visible", ["Arquivos"] = "Files", ["Pasta"] = "Folder", ["Procurar"] = "Search",
        ["Todos"] = "All", ["Enviar"] = "Send", ["Baixar"] = "Download", ["Abrir"] = "Open", ["Fechar"] = "Close",
        ["Cancelar"] = "Cancel", ["Entrar"] = "OK", ["Aceitar"] = "Accept", ["Recusar"] = "Decline",
        ["Pausar"] = "Pause", ["Copiado"] = "Copied", ["Cancelado"] = "Cancelled",
        ["SEGURANÇA"] = "SECURITY", ["PASTAS SINCRONIZADAS"] = "SYNCED FOLDERS", ["APARÊNCIA"] = "APPEARANCE",
        ["Remover todos da lista"] = "Remove all from the list", ["Disponibilizar arquivos na rede"] = "Share files on the network",
        ["Enviar arquivos…"] = "Send files…", ["Enviar pasta…"] = "Send folder…", ["Enviar texto…"] = "Send text…",
        ["Sincronizar uma pasta com este aparelho…"] = "Sync a folder with this device…",
        ["Abrir LAN Transfer"] = "Open LAN Transfer", ["Disponibilizar arquivos…"] = "Share files…", ["Sair"] = "Quit",

        // ---- expressões usadas em textos montados
        ["Recebidos e baixados vão para:"] = "Received and downloaded files go to:",
        ["Este PC:"] = "This PC:",
        ["Protegido:"] = "Protected:",
        ["só aparelhos de confiança"] = "trusted devices only",
        ["Só compartilha com aparelhos de confiança"] = "Only shares with trusted devices",
        ["Protegido por PIN"] = "PIN protected",
        ["arquivo(s) em pacote para"] = "file(s) in a bundle to",
        ["arquivo(s) enviados para"] = "file(s) sent to",
        ["quer sincronizar"] = "wants to sync",
        ["arquivo(s) de uma pasta"] = "file(s) from a folder",
        ["quer enviar"] = "wants to send",
        ["serão salvos em"] = "will be saved in",
        ["arquivo(s) para"] = "file(s) to",
        ["arquivo(s)"] = "file(s)",
        ["Aguardando"] = "Waiting for", ["aceitar…"] = "to accept…",
        ["Sincronizando com"] = "Syncing with",
        ["Na fila"] = "Queued", ["Baixando"] = "Downloading", ["Enviando"] = "Sending", ["Recebendo de"] = "Receiving from",
        ["Recebendo"] = "Receiving", ["Sendo baixado"] = "Being downloaded", ["baixado por"] = "downloaded by",
        ["enviado para"] = "sent to", ["recebido de"] = "received from", ["baixado de"] = "downloaded from",
        ["texto recebido de"] = "text received from", ["texto enviado para"] = "text sent to",
        ["Texto de"] = "Text from", ["copiado"] = "copied", ["clique para copiar"] = "click to copy",
        ["Pausado em"] = "Paused at", ["toque em ▶ para continuar de onde parou"] = "press ▶ to resume where it stopped",
        ["toque em ▶ para continuar"] = "press ▶ to resume",
        ["faltam"] = "left", ["Falhou:"] = "Failed:", ["Sem resposta:"] = "No answer:", ["recusou o envio"] = "declined the transfer",
        ["última vez"] = "last time", ["ainda não sincronizada"] = "not synced yet", ["arquivo(s) enviados"] = "file(s) sent",
        ["pasta"] = "folder", ["de"] = "of", ["para"] = "to",
        ["Download concluído"] = "Download complete", ["Arquivos salvos em"] = "Files saved in",
        ["Envio concluído"] = "Transfer complete", ["Arquivo baixado"] = "File downloaded", ["baixou"] = "downloaded",
        ["Arquivos recebidos"] = "Files received", ["Salvos em"] = "Saved in", ["Recebendo arquivos"] = "Receiving files",
        ["está enviando"] = "is sending", ["Texto recebido"] = "Text received",
        ["Disponível na rede"] = "Available on the network", ["agora aparecem para os outros aparelhos."] = "now show up on other devices.",
        ["Pasta disponível na rede"] = "Folder available on the network", ["com as subpastas."] = "with subfolders.",
        ["Pasta sincronizada"] = "Folder synced", ["Nada para enviar"] = "Nothing to send", ["Não há arquivos."] = "There are no files.",
        ["Compartilhamento pausado"] = "Sharing paused", ["Pedido de envio"] = "Transfer request",
        ["PIN incorreto."] = "Wrong PIN.", ["PIN de"] = "PIN for", ["Enviar texto para"] = "Send text to",
        ["O texto está na área de transferência."] = "The text is in the clipboard.",
        ["A página vai pedir o PIN"] = "The page will ask for PIN", ["Outros endereços deste PC:"] = "Other addresses of this PC:",
        ["recebido"] = "received",
    };

    private static Regex? _rx;

    public static void Install(string language)
    {
        English = language == "en";
        if (!English) return;
        var keys = En.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape);
        _rx = new Regex(@"(?<![\p{L}\d])(" + string.Join("|", keys) + @")(?![\p{L}\d])", RegexOptions.Compiled);
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
    }

    /// <summary>Traduz um texto (frases conhecidas e expressões dentro de textos montados).</summary>
    public static string T(string s) => !English || _rx == null || string.IsNullOrEmpty(s) ? s : _rx.Replace(s, m => En[m.Value]);

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        Fix(d, TextBlock.TextProperty);
        Fix(d, ContentControl.ContentProperty);
        Fix(d, FrameworkElement.ToolTipProperty);
        Fix(d, Wpf.Ui.Controls.TextBox.PlaceholderTextProperty);
        Fix(d, HeaderedItemsControl.HeaderProperty);
        Fix(d, Window.TitleProperty);
    }

    private static void Fix(DependencyObject d, DependencyProperty p)
    {
        if (!p.OwnerType.IsInstanceOfType(d) && d.ReadLocalValue(p) == DependencyProperty.UnsetValue) return;
        if (BindingOperations.IsDataBound(d, p)) return;
        if (d.GetValue(p) is string s && s.Length > 0)
        {
            var t = T(s);
            if (t != s) d.SetValue(p, t);
        }
    }
}
