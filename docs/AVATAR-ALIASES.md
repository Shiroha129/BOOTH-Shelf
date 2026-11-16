# Avatar names used in product compatibility

Reviewed on 2026-11-17 using public BOOTH product metadata. `product-details.js` contains the small, offline name table; product details never fetch another model page to resolve a name.

Only aliases referring to the same model are combined. Sharing a body does not make two named avatars the same model. Matching ignores case and fullwidth spelling, folds katakana to hiragana, and removes surrounding sales text, honorifics and compatibility suffixes. It does not use approximate spelling matches or scan arbitrary description prose.

The first name is the display name. The ID identifies the original model and can resolve an unfamiliar spelling when a compatibility-list entry links to that model. Each source can be read at `https://booth.pm/ja/items/<ID>`.

| Display name | Other observed names | Original BOOTH item ID |
| --- | --- | --- |
| Linne | リンネ, りんね | 8355117 |
| Shuan | しゅあん | 6669962 |
| Manuka | マヌカ | 5058077 |
| Sio | しお | 5650156 |
| Shinano | しなの | 6106863 |
| Milfy | ミルフィ, みるふぃ | 6571299 |
| Eku | エク | 7328764 |
| Chiffon | シフォン | 5354471 |
| Chocolat | ショコラ, しょこら | 6405390 |
| Mao | 真央, まお | 6846646 |
| Rurune | ルルネ | 5957830 |
| Mayo | まよ | 8122803 |
| Kipfel | キプフェル | 5813187 |
| Powari | ぽわり | 8352244 |
| Airi | 愛莉 | 6082686 |
| Michella | ミシェラ | 8916626 |
| Misaki | 海咲, みさき | 8325804 |
| Marycia | マリシア | 6305948 |
| Lumina | ルミナ | 7502898 |
| Vivian | ヴィヴィアン, 비비안 | 8910263 |
| Selestia | セレスティア | 4035411 |
| Mizuki | 瑞希 | 5132797 |
| Kaguya | 輝夜, かぐや | 8562330 |
| Lime | ライム | 4876459 |
| Plum | プラム | 7770415 |
| Milltina | ミルティナ | 6538026 |
| Ichigo | イチゴ | 7328789 |
| Ramune | ラムネ, ラムネ(えも研) | 7699667 |
| Kumaly | ひきくまりのクマリ, クマリ | 7928592 |
| Kikyo | Kikyou, 桔梗 | 3681787 |
| Moe | 萌 | 4667400 |
| Maya | 舞夜 | 3390957 |
| Shinra | 森羅 | 4707634 |
| Rindo | Rindou, 竜胆 | 3443188 |
| Salt | ソルト | 8962830 |
| Leefa | リーファ | 3659436 |
| Mamehinata | まめひなた | 4340548 |
| Rusk | ラスク | 2559783 |
| Grus | — | 3190100 |
| Miredo | ミレド | 8906276 |
| Ririka | りりか | 6373683 |

The original models' titles, introductory text and tags supply the Japanese names and most Latin names. These public products supply additional paired names and examples of their use:

- `https://ssugana.booth.pm/items/9117869`: Luciel Hair, 20-model compatibility list, including joined Japanese/Latin spellings. The regression fixture retains the model-list excerpt and substitutes short text for unrelated sections.
- `https://booth.pm/ja/items/9121657`: BunnyTre, 20-model references and purchase variants; also demonstrates that shape-key sections must not contribute model names.
- `https://booth.pm/ja/items/5213334`: Layered hush cut Hair, Latin names for Kikyo, Moe, Selestia and Shinra, alongside links to the original models. The Shinra original returned 404 during this check; its identity is retained from these explicit product references, not inferred from a similar spelling.
- `https://booth.pm/ja/items/5359699`: Night in Yoshiwara, paired variants and Kikyou spelling in the seller's tags.
- `https://booth.pm/ja/items/3428407`: Disorder_Cloth, Rindo in the description and Rindou in the seller's tags, paired with 竜胆.
- `https://booth.pm/ja/items/6983353`: paired Leefa/Lunalitt variants and Japanese model tags.
- `https://booth.pm/ja/items/5068109`: しょこら in the seller's model tags.

Unknown names remain as written when explicitly listed. Do not merge models based on a shared body, translation of an ordinary word (for example Sio versus Salt), or similar-looking names (Mao/Mayo/Maya, Milfy/Milltina). Add new spellings only with a model identity and a public source.
