# Usage Widget

Widget Windows nativo em C# / WinForms, vinculado à área de trabalho e arrastável, com Codex, Gemini (Antigravity) e Claude Desktop. Sem Electron, Tauri ou WebView. Porcentagens representam o saldo da menor cota retornada, nunca uma soma dos limites.

Execute `dist/widget/Usage.exe`. Mantenha a pasta completa: o runtime .NET está incluído, sem extração para o C:. Arquivos de posição/preferências e temporários ficam junto ao executável. O aplicativo recusa execução a partir do C:.

- Um ícone na bandeja mostra/oculta o widget. Sem botão na barra de tarefas. O × oculta; Exit no menu encerra o app.
- Arraste o widget para mover; a posição é salva e ajustada se o monitor deixar de existir.
- Passe o mouse sobre a porcentagem para ver detalhes das cotas.
- Botão direito: Refresh, Start with Windows e Exit. Interface em inglês; rodapé compacto Checked HH:mm.
- Inicialização automática: registro `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, valor `UsageWidget`, apontando para o executável no G:. Não é criado atalho no C:.
- Codex: consulta `account/rateLimits/read` pelo app-server existente, usando `CODEX_HOME`.
- Gemini: consulta somente o serviço local do Antigravity aberto e autenticado.
- Claude Desktop: tenta consultar a API de uso pela sessão existente do Desktop. Lê somente sessionKey e lastActiveOrg, em modo somente leitura; a chave é mantida apenas em memória, enviada somente para claude.ai e nunca copiada para disco. O banco fica bloqueado enquanto o Desktop está aberto: para conectar, é necessário encerrá-lo brevemente e usar Refresh no Usage, depois reabri-lo. Após reiniciar o Usage essa conexão pode precisar ser refeita. Se a consulta direta falhar, o histórico local aparece explicitamente como Cached HH:mm, com o horário original e a causa no tooltip. A consulta ao vivo ainda não foi validada nesta máquina porque o usuário está utilizando o Desktop. O histórico não é uma fonte garantida de atualização contínua.

As consultas ocorrem a cada dois minutos. Falhas aparecem como indisponíveis; amostras vazias do Claude não reutilizam silenciosamente dados anteriores de outra conta.

## Compilar e verificar

```powershell
./build.ps1
# Checagens de parsing e consultas reais; saída sem credenciais em artifacts/check.json:
./dist/widget/Usage.exe --check
# Ativar início automático para o executável publicado:
./dist/widget/Usage.exe --enable-startup
```

O script usa `.build/` no G: para os caches e temporários. Requer SDK .NET 10 em Windows. A opção de inicialização também está no menu do widget.



Os logos foram baixados do SVGL: https://svgl.app/library/codex_dark.svg, https://svgl.app/library/gemini.svg e https://svgl.app/library/claude-ai-icon.svg. Os SVGs originais ficam em assets/; a rasterização ocorre apenas na compilação dos assets com tools/render-icons.cjs, sem dependência de navegador no app.

O widget é uma janela filha do desktop do Explorer; outros aplicativos ficam à frente. --check-widget valida o vínculo nativo, a ausência de topmost/barra de tarefas e gera uma prévia em artifacts/.

# HowsMyUsage
