# Instalar e atualizar o Night Shift

Alvo: **Windows 10/11 x64**. O pacote inclui o runtime .NET 10; você não precisa instalar o SDK para usar o aplicativo.

## Instalação nova

1. Na página do repositório, abra **Releases**. Se houver uma release 0.7.1 publicada, baixe `Night-Shift-v0.7.1-Windows-x64.zip`.
2. Extraia **todo o ZIP** para uma pasta estável. Não execute o aplicativo de dentro do arquivo compactado.
3. Abra `NightShift.exe`. Preserve `nightshift.portable` para usar os dados portáteis.
4. Instale e autentique a CLI do provedor escolhido pelos procedimentos oficiais.
5. No Night Shift, selecione Codex ou Claude, conecte e escolha o projeto.

Não há download público presumido por este documento. Se Releases estiver vazio, use um pacote fornecido pelo mantenedor ou compile o código.

## Provedores

- **Codex CLI:** aceita `codex.exe` e o `codex.cmd` do npm oficial, com Node no PATH. Se necessário, escolha o executável em **Ações → Ajustes**. **Entrar com ChatGPT** abre o login oficial; depois desconecte e conecte novamente.
- **Claude Code local:** requer instalação nativa **2.1.234 ou superior**, autenticada. Claude Desktop, chat normal e Cowork não substituem a CLI.
- **Codex Desktop / VS Code:** somente com app-server local compartilhado já acessível a ambos os clientes. Informe o WebSocket em **Ações → Conexão e plataformas**, conecte, atualize e vincule a sessão. Escolher a plataforma não configura nem detecta o outro cliente.

Os requisitos e limites completos estão em [COMPATIBILITY.md](COMPATIBILITY.md). Night Shift não instala os provedores nem administra suas credenciais.

## Atualização de uma instalação existente

1. Feche o aplicativo.
2. Faça backup da instalação e dos seus dados.
3. Extraia o pacote novo em uma pasta temporária.
4. Substitua **somente `NightShift.exe`** na pasta de instalação atual.
5. Preserve `Dados`, `nightshift.portable` e o caminho original da instalação.

Uma instalação em outra pasta cria dados separados. No modo não portátil, o caminho continua sendo `%LOCALAPPDATA%/NightShift-v0.6`, preservando os dados legados. Os caches Claude são separados; os caminhos legados Codex continuam compatíveis.

Não publique backups, anexos, histórico ou credenciais em issues.

## Compilar a partir do código

No Windows com SDK .NET 10 e PowerShell, abra a raiz do projeto:

```powershell
./scripts/Build.ps1
```

O script executa a suíte lógica e publica um executável Windows x64 self-contained. O resultado fica em `artifacts/build-DATA-ID`, com `Night-Shift-v0.7.1-Windows-x64.zip`. Cada compilação usa uma pasta nova.

Para executar também os testes nativos WPF:

```powershell
./scripts/Build.ps1 -ValidateUI
```

Este documento descreve os comandos disponíveis; não afirma que foram executados nesta preparação do repositório. Consulte [VALIDATION.md](VALIDATION.md) para os resultados registrados das entregas.

## Se algo não funcionar

Confira se a CLI está instalada, autenticada e no caminho configurado. Limites ausentes são exibidos como **Não informado**. Se um envio ficar sem confirmação, confira o histórico antes de reenviar: o agente pode já ter executado a tarefa.

Para uma sessão externa com pergunta ou permissão pendente, use o cliente ou terminal de origem. Consulte o [manual completo](USER-GUIDE.pt-BR.md) antes de retomar ou reconectar.
