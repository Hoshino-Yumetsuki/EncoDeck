// SPDX-License-Identifier: GPL-3.0-only
// Model facts from Edifier Connect 8.5.3 assets/products_release.json.
using EncoDeck.Core.Profiles;

namespace EncoDeck.Protocol.Brands.Edifier;

public static partial class EdifierProfiles
{
    private static IEnumerable<IDeviceProfile> CreateTwsProfiles() =>
    [
        new EdifierProfile("edifier-170", "EDIFIER Lolli 3", "lolli3", false, isTws: true, hasSpp: true, gattService: "48097001-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-235", "EDIFIER Lolli3 ANC", "lolli3anc", false, isTws: true, hasSpp: true, gattService: "48099b01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-180", "EDIFIER LolliPods 2022", "lollipods2022", false, isTws: true, hasSpp: true, gattService: "48097601-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-203", "EDIFIER Lolli Pro 2", "lollipro2", false, isTws: true, hasSpp: true, gattService: "48099701-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-110", "EDIFIER Lolli Pods Pro", "lollipodspro", false, isTws: true, hasSpp: false, gattService: "48092401-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-401", "EDIFIER LolliClip", "lolliclip", false, isTws: true, hasSpp: true, gattService: "4809ee01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-250", "EDIFIER ATOMBUDS at03", "atombudsat03", false, isTws: true, hasSpp: true, gattService: "4809b301-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-258", "EDIFIER Atombuds AS02", "atombudsas02", false, isTws: true, hasSpp: true, gattService: "4809b201-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-264", "EDIFIER AtomBuds", "atombuds", false, isTws: true, hasSpp: true, gattService: "4809b501-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-276", "EDIFIER ATOMBUDS A1", "atombudsa1", false, isTws: true, hasSpp: true, gattService: "48091b01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-365", "EDIFIER Lolli Pro 3", "lollipro3", false, isTws: true, hasSpp: true, gattService: "4809d601-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-382", "EDIFIER Lolli Pro 2S", "lollipro2s", false, isTws: true, hasSpp: true, gattService: "4809ed01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-387", "EDIFIER Atom Max", "atommax", false, isTws: true, hasSpp: true, gattService: "4809eb01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-400", "EDIFIER AtomBuds Plus", "atombudsplus", false, isTws: true, hasSpp: true, gattService: "48090701-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-467", "EDIFIER Lolli Pro 3i", "lollipro3i", false, isTws: true, hasSpp: true, gattService: "48093401-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-478", "EDIFIER Lolli ANC", "lollianc", false, isTws: true, hasSpp: true, gattService: "48094201-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-479", "EDIFIER Lolli Pro 5", "lollipro5", false, isTws: true, hasSpp: true, gattService: "48093f01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-482", "EDIFIER Atom Lace", "atomlace", false, isTws: true, hasSpp: true, gattService: "48094e01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-485", "EDIFIER Atom Dot", "atomdot", false, isTws: true, hasSpp: true, gattService: "48093c01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-519", "EDIFIER LolliClip AI", "lolliclipai", false, isTws: true, hasSpp: true, gattService: "48092803-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-522", "EDIFIER Lolli Air", "lolliair", false, isTws: true, hasSpp: true, gattService: "48091403-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-536", "EDIFIER Lolli Pro SE", "lolliprose", false, isTws: true, hasSpp: true, gattService: "48092f03-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-541", "EDIFIER Atom CC", "atomcc", false, isTws: true, hasSpp: true, gattService: "48090603-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-546", "EDIFIER Atom ANC", "atomanc", false, isTws: true, hasSpp: true, gattService: "4809e201-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-547", "EDIFIER Atom Evo", "atomevo", false, isTws: true, hasSpp: true, gattService: "48093503-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-578", "EDIFIER Lolli5 ANC", "lolli5anc", false, isTws: true, hasSpp: true, gattService: "48093203-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-586", "EDIFIER LolliClip SE", "lolliclipse", false, isTws: true, hasSpp: true, gattService: "48096603-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-605", "EDIFIER Atom Max 2.0", "atommax20", false, isTws: true, hasSpp: true, gattService: "48098903-1a48-11e9-ab14-d663bd873d93"),
    ];
}
