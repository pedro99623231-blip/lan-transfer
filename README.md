# LAN Transfer

Transfira arquivos, pastas e textos entre PC Windows, celular Android, Linux e Mac que estejam na mesma rede (Wi-Fi ou cabo). Sem nuvem, sem cadastro, sem cabo USB.

## Baixar

Pegue a versão mais recente em **[Releases](../../releases/latest)**:

| Sistema | Arquivo |
|---|---|
| Windows 10/11 (64 bits) | `LANTransfer-Setup-5.0.0.exe` |
| Android 10 ou mais novo | `LANTransfer-5.0.0.apk` |
| Linux e Mac | `lan_transfer.py` (pasta [`linux-mac`](linux-mac)) |

No Android, permita "instalar apps desconhecidos" para o navegador ou gerenciador de arquivos na hora de abrir o APK.

## O que ele faz

- **Encontra os outros aparelhos sozinho** na rede, inclusive quando há mais de um roteador.
- Cada aparelho **disponibiliza** arquivos e pastas, e os outros veem a lista e baixam o que quiserem. No PC os arquivos são compartilhados no lugar, sem cópia.
- **Envio direto**: mande arquivos ou textos para outro aparelho, que aceita ou recusa.
- Velocidade, tempo restante, pausar, cancelar e **retomar downloads interrompidos**.
- Pastas sincronizadas, miniaturas, pré-visualização e histórico.
- **PIN opcional** e modo "só aparelhos confiáveis".
- Conexão **HTTPS** entre os apps, com certificado verificado pela impressão digital anunciada na descoberta.
- **QR code** para quem não tem o app: abre uma página no navegador para baixar e enviar.
- Tema claro/escuro e idioma português/inglês.
- Windows: ícone na bandeja, iniciar com o Windows e "Disponibilizar no LAN Transfer" no botão direito do Explorer.

## Estrutura

| Pasta | Conteúdo |
|---|---|
| [`windows`](windows) | App WPF em C# (.NET 8, WPF-UI, Kestrel, QRCoder) e instalador Inno Setup |
| [`android`](android) | App em Kotlin com Jetpack Compose (Material 3) e OkHttp |
| [`linux-mac`](linux-mac) | Script único em Python 3.8+ só com a biblioteca padrão, com interface no navegador |

## Compilar

**Windows** (precisa do .NET SDK 8 e do Inno Setup 6):

```powershell
cd windows
powershell -ExecutionPolicy Bypass -File build.ps1
```

O instalador sai em `windows/output/`.

**Android** (precisa do Android SDK e JDK 17):

```powershell
cd android
powershell -ExecutionPolicy Bypass -File build-apk.ps1
```

Ou, em qualquer sistema, `./gradlew assembleRelease` com `sdk.dir` no `local.properties`. O APK sai em `android/output/`. O build de release é assinado com a chave de debug local, para poder ser instalado direto.

**Linux/Mac**: não precisa compilar. Rode `python3 linux-mac/lan_transfer.py`. Veja o [LEIA-ME](linux-mac/LEIA-ME.md).

## Rede e firewall

Os apps usam a porta **UDP 48555** para se encontrarem e **TCP 8000 a 8019** (HTTP) e a mesma faixa +100 (HTTPS) para as transferências. O instalador do Windows libera isso no firewall. No Linux com `ufw`, libere essas portas manualmente.

## Licença

[MIT](LICENSE)
