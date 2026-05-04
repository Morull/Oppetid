# Dalane Kraft logo

App-en viser logoen i topp-baren via `<img src="img/dalane-kraft-logo.png">`.
Hvis fila mangler, faller vi automatisk tilbake til en heksagon-formet
plassholder med "DALANE KRAFT"-tekst og brand-fargene fra logoen.

## Slik installerer du den ekte logoen

1. Last ned PNG-en fra Dalane Krafts nettside (logget inn fra et browser-vindu):

   ```
   https://dalane-kraft.no/wp-content/uploads/2025/07/DE_logo_Kraft_cmyk_horisontal.png
   ```

   eller den kompakte varianten:

   ```
   https://dalane-kraft.no/wp-content/uploads/2025/07/Dalane_kraft.png
   ```

2. Lagre fila som `dalane-kraft-logo.png` i denne mappa
   (`src/KraftverkUptime.Web/wwwroot/img/`).

3. Restart appen — logoen vises i topp-baren neste gang du laster siden.

## Hvorfor manuell nedlasting

Sandbox-en blokkerer at agenten kopierer eksterne filer av "uverifisert
opphav" inn i repoet. Du kan trygt laste filen selv siden du har
verifisert at logo-asseten tilhører din organisasjon.

## Mørk variant

Hvis du vil ha forskjellig logo i mørk-modus, lagre `dalane-kraft-logo-dark.png`
i samme mappe. Vi kan deretter utvide MainLayout til å bytte basert på
`App.IsDarkMode`-flagget.
