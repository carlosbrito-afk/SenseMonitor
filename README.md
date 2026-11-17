# SenseMonitor

Central de controle leve para notebooks gamer da Acer (Nitro, Nitro V, Predator e afins), feita para
substituir o NitroSense / PredatorSense. Fica na bandeja do Windows e abre com a tecla NitroSense.

> Projeto independente. Não é afiliado, patrocinado nem aprovado pela Acer. "Acer", "Nitro",
> "NitroSense" e "Predator" são marcas dos seus donos e aparecem aqui só para dizer com quais
> notebooks o app é compatível.

## O que faz

- **Monitor:** temperatura e uso de CPU e GPU (gráficos de 10 minutos), rotação das ventoinhas, memória.
- **Limpar RAM** de verdade (esvazia working sets, lista modificada e cache em espera, como o RAMMap).
- **Modos:** Silencioso, Dia a dia e Desempenho, com conferência no firmware.
- **Ventoinha:** Automático, Manual por ventoinha, Curva editável e Máximo — liberados só depois de um
  teste no seu próprio notebook.
- **Segurança térmica:** proteção a 90 °C, vigia, e volta ao automático ao fechar, suspender ou travar.
- **Saúde:** histórico de telas azuis com o nome do erro, saúde da bateria e do disco.
- **Manutenção:** programas que iniciam com o Windows (com desfazer), limpeza de temporários, caches
  e logs (nunca toca em documentos, despejos de tela azul nem logs do sistema), reparo do Windows.
- **Testes embutidos:** `--autoteste` (confere cada função sem mudar nada) e `--teste-desempenho`
  (mede se cada modo rende mais ou menos que o Dia a dia).

Sem driver, sem serviço, sem nada do fabricante: só chamadas ao firmware pela interface WMI
`root\WMI AcerGamingFunction` e APIs do Windows.

## Compatibilidade

Validado num Acer Nitro V15. Outros modelos da família usam a mesma interface de firmware, mas com
diferenças (veja `docs/pesquisa-modelos-acer.md`). Em modelos não confirmados, o controle de
ventoinha e de modos fica travado até passar no teste guiado no próprio aparelho.

## Compilar

Precisa só do Windows 10/11 (o compilador C# do .NET Framework 4.8 já vem nele). Sem SDK, sem NuGet.

```
build.cmd          compila tudo (app, testes e sonda)
bin\ForjaTests.exe roda os testes
```

## Instalar

O app roda como administrador (o firmware só responde a processos elevados), então a cópia de uso fica
em `%ProgramFiles%\SenseMonitor`. Num PowerShell **como administrador**, na pasta do projeto:

```
powershell -ExecutionPolicy Bypass -File .\deploy.ps1
```

Depois de instalar, rode o teste das ventoinhas uma vez com você ouvindo o notebook:

```
"%ProgramFiles%\SenseMonitor\SenseMonitor.exe" --validar-ventoinha
```

## Autor

Criado por **carlosbrito-afk** (ideia, direção do produto, testes no hardware), com código escrito
com a ajuda do Claude (Anthropic).

## Licença

Domínio público — veja `LICENSE` (The Unlicense). Use, copie, modifique e distribua como quiser.
