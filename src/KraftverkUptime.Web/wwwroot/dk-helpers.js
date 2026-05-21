// Små JS-interop-hjelpere kalt fra Blazor-sidene. Holdes minimal — alt
// som kan gjøres i C# bør holdes i C#.

window.dkEffPunkter = null;

/**
 * Lagrer en parallel-liste over tidspunkter for Effektivitet-scatter-tooltipen.
 * Skala-indeksene i ApexCharts matcher rekkefølgen i denne lista.
 */
window.dkEffPunkterSet = function (arr) {
    window.dkEffPunkter = arr;
};

/**
 * Scroll-til-elementet med gitt id. Brukes til drilldown-flow når brukeren
 * klikker en bin og vil se den filtrerte time-tabellen under.
 */
window.scrollToElement = function (id) {
    var el = document.getElementById(id);
    if (el) {
        el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
};

/**
 * Klientside-nedlasting av tekst-blob. Brukes til CSV-eksport så vi slipper
 * et eget API-endepunkt for å serialisere data vi allerede har i klienten.
 */
window.downloadTextFile = function (filename, content) {
    var blob = new Blob([content], { type: 'text/csv;charset=utf-8' });
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};
