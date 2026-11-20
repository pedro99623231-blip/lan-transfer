# LAN Transfer para Linux e Mac

[English](README.md)

## Versão pronta (recomendado)

Baixe o arquivo do seu sistema na página de [Releases](https://github.com/pedro99623231-blip/lan-transfer/releases/latest):

| Sistema | Arquivo |
|---|---|
| Linux (64 bits) | `LANTransfer-5.0.0-linux-x64.tar.gz` |
| Mac com Intel | `LANTransfer-5.0.0-macos-intel.tar.gz` |
| Mac com Apple Silicon (M1 em diante) | `LANTransfer-5.0.0-macos-apple-silicon.tar.gz` |

Extraia e rode no terminal:

```
./lan-transfer
```

**Mac:** o app não é assinado pela Apple, então na primeira vez o macOS bloqueia. Rode uma vez, na pasta extraída:

```
xattr -d com.apple.quarantine lan-transfer
```

## Rodando pelo código-fonte

Um único arquivo, `lan_transfer.py`, que só precisa do Python 3.8 ou mais novo (já vem no Mac e na maioria dos Linux). Não instala nada.

```
python3 lan_transfer.py                 # disponibiliza a pasta ~/LAN Transfer/Compartilhar
python3 lan_transfer.py foto.jpg Pasta  # disponibiliza também esses arquivos e pastas
```

A tela abre no navegador (http://127.0.0.1:8000/app). Lá você:
- vê os outros aparelhos com LAN Transfer (PC Windows, Android, outros Mac/Linux) e baixa os arquivos deles;
- envia arquivos e textos para eles;
- aceita ou recusa o que os outros quiserem te mandar (e copia os textos recebidos).

Tudo que chega vai para `~/Downloads/LAN Transfer`. Downloads interrompidos continuam de onde pararam.
Quem não tem o app pode abrir no navegador o endereço que aparece no terminal.

Para fechar: Ctrl+C no terminal. Use `--no-browser` para não abrir o navegador.

Observações: esta versão conversa sem criptografia (HTTP); os apps do Windows e Android caem automaticamente para HTTP com ela. No Linux, se houver firewall (ufw), libere a porta UDP 48555 e TCP 8000-8019.
