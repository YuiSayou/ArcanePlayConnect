using System.Collections.Generic;
using System.Linq;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.Core;

/// <summary>
/// Static catalog of all known TikTok gifts with names, coin prices, and image URLs.
/// </summary>
public static class TikTokGiftLibrary
{
    private static List<TikTokGift>? _gifts;

    public static IReadOnlyList<TikTokGift> All => _gifts ??= BuildGiftList();

    /// <summary>Search gifts by partial name (case-insensitive).</summary>
    public static List<TikTokGift> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return All.ToList();

        var q = query.Trim();
        return All
            .Where(g => g.Name.Contains(q, System.StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Name.StartsWith(q, System.StringComparison.OrdinalIgnoreCase))
            .ThenBy(g => g.CoinPrice)
            .ToList();
    }

    /// <summary>Find an exact gift by name (case-insensitive).</summary>
    public static TikTokGift? FindByName(string name)
    {
        return All.FirstOrDefault(g =>
            string.Equals(g.Name, name, System.StringComparison.OrdinalIgnoreCase));
    }

    private static List<TikTokGift> BuildGiftList()
    {
        var gifts = new List<TikTokGift>();
        void Add(string name, int price, string url) =>
            gifts.Add(new TikTokGift { Name = name, CoinPrice = price, ImageUrl = url });

        // ?? Special Interactions (free) ??
        Add("Like", 0, "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAxMjggMTI4IiB3aWR0aD0iMTI4IiBoZWlnaHQ9IjEyOCI+PGRlZnM+PGxpbmVhckdyYWRpZW50IGlkPSJsZyIgeDE9IjAiIHkxPSIwIiB4Mj0iMSIgeTI9IjEiPjxzdG9wIG9mZnNldD0iMCUiIHN0b3AtY29sb3I9IiNmZjNiNWMiLz48c3RvcCBvZmZzZXQ9IjEwMCUiIHN0b3AtY29sb3I9IiNmZjAwNTAiLz48L2xpbmVhckdyYWRpZW50PjwvZGVmcz48Y2lyY2xlIGN4PSI2NCIgY3k9IjY0IiByPSI2MCIgZmlsbD0iIzFhMWEyZSIvPjxwYXRoIGQ9Ik02NCAxMDggQzQwIDg4IDE2IDY4IDE2IDQ4IEMxNiAzMCAzMCAxOCA0NiAxOCBDNTQgMTggNjAgMjIgNjQgMjggQzY4IDIyIDc0IDE4IDgyIDE4IEM5OCAxOCAxMTIgMzAgMTEyIDQ4IEMxMTIgNjggODggODggNjQgMTA4WiIgZmlsbD0idXJsKCNsZykiLz48L3N2Zz4=");
        Add("Follow", 0, "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAxMjggMTI4IiB3aWR0aD0iMTI4IiBoZWlnaHQ9IjEyOCI+PGRlZnM+PGxpbmVhckdyYWRpZW50IGlkPSJsZyIgeDE9IjAiIHkxPSIwIiB4Mj0iMSIgeTI9IjEiPjxzdG9wIG9mZnNldD0iMCUiIHN0b3AtY29sb3I9IiMwMGM4ZmYiLz48c3RvcCBvZmZzZXQ9IjEwMCUiIHN0b3AtY29sb3I9IiNiNDAwZmYiLz48L2xpbmVhckdyYWRpZW50PjwvZGVmcz48Y2lyY2xlIGN4PSI2NCIgY3k9IjY0IiByPSI2MCIgZmlsbD0iIzFhMWEyZSIvPjxjaXJjbGUgY3g9IjUyIiBjeT0iNDQiIHI9IjE4IiBmaWxsPSJ1cmwoI2xnKSIvPjxwYXRoIGQ9Ik0yMiAxMDAgQzIyIDc4IDM2IDY2IDUyIDY2IEM2OCA2NiA4MiA3OCA4MiAxMDBaIiBmaWxsPSJ1cmwoI2xnKSIvPjxsaW5lIHgxPSI5NiIgeTE9IjU2IiB4Mj0iOTYiIHkyPSI4NCIgc3Ryb2tlPSIjMDBmZjg4IiBzdHJva2Utd2lkdGg9IjciIHN0cm9rZS1saW5lY2FwPSJyb3VuZCIvPjxsaW5lIHgxPSI4MiIgeTE9IjcwIiB4Mj0iMTEwIiB5Mj0iNzAiIHN0cm9rZT0iIzAwZmY4OCIgc3Ryb2tlLXdpZHRoPSI3IiBzdHJva2UtbGluZWNhcD0icm91bmQiLz48L3N2Zz4=");

        // ?? TikTok Gifts ??
        Add("Rose", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/eba3a9bb85c33e017f3648eaf88d7189~tplv-obj.webp");
        Add("TikTok", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/802a21ae29f9fae5abe3693de9f874bd~tplv-obj.webp");
        Add("GG", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/3f02fa9594bd1495ff4e8aa5ae265eef~tplv-obj.webp");
        Add("You're awesome", 1, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/e9cafce8279220ed26016a71076d6a8a.png~tplv-obj.webp");
        Add("Pop", 1, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/0b4f61e8ab637f11449300d03929ef87.png~tplv-obj.webp");
        Add("Creeper", 1, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/d686d45bd66e16b0aca8b0e5eb52a977.png~tplv-obj.webp");
        Add("Ice Cream Cone", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/968820bc85e274713c795a6aef3f7c67~tplv-obj.webp");
        Add("Love you so much", 1, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/fc549cf1bc61f9c8a1c97ebab68dced7.png~tplv-obj.webp");
        Add("Wink wink", 1, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/4a68411b3e92fc2bf68d458d5f906b74.png~tplv-obj.webp");
        Add("Freestyle", 1, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/1f5ca5cfb4b98c2761fb85987f47c641.png~tplv-obj.webp");
        Add("Oldies", 1, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/77f6ab69b0b03bda98a0a3d2bfdeb46f.png~tplv-obj.webp");
        Add("Cake Slice", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/f681afb4be36d8a321eac741d387f1e2~tplv-obj.webp");
        Add("Glow Stick", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/8e1a5d66370c5586545e358e37c10d25~tplv-obj.webp");
        Add("Heart Me", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/d56945782445b0b8c8658ed44f894c7b~tplv-obj.webp");
        Add("Congratulations", 1, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/8e73d843b23a9e68f8d3cf8c46fc0bee.png~tplv-obj.webp");
        Add("So Cute", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/d40d31241efcf57c630e894bb3007b8a.png~tplv-obj.webp");
        Add("Thumbs Up", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/570a663e27bdc460e05556fd1596771a~tplv-obj.webp");
        Add("Heart", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/dd300fd35a757d751301fba862a258f1~tplv-obj.webp");
        Add("Love you", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/ab0a7b44bfc140923bb74164f6f880ab~tplv-obj.webp");
        Add("Heart Puff", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/68c21ef420f49b87543de354b2e30b8d.png~tplv-obj.webp");
        Add("Blue Heart", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/558e5c35af66f03c88ee426d3f0df231.png~tplv-obj.webp");
        Add("Flame heart", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/b199d028d5beb081fe16edcf77db0830.png~tplv-obj.webp");
        Add("Power hug", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/9578adce6e3da2d211583212bdfd1b0e.png~tplv-obj.webp");
        Add("Squirrel", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/05ea964bf24ff849df2608e9116e0c87.png~tplv-obj.webp");
        Add("Chilli Pepper", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/226bd28d1fb2c06be7086de99220968e.png~tplv-obj.webp");
        Add("Glass of Airan", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/065105b39fc8bba4902be568b09b63f7.png~tplv-obj.webp");
        Add("Tulip", 1, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/234b32efd0ebfafbe355b0d4d2dcf135.png~tplv-obj.webp");
        Add("Music Album", 1, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/2a5378fbb272f5b4be0678084c66bdc1.png~tplv-obj.webp");
        Add("Graduation Bouquet", 1, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/4228f731f6094d74be83e47bffc97898.png~tplv-obj.webp");
        Add("Go Popular", 1, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/b342e28d73dac6547e0b3e2ad57f6597.png~tplv-obj.webp");
        Add("Club Cheers", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/6a934c90e5533a4145bed7eae66d71bd.png~tplv-obj.webp");
        Add("Wink Charm", 1, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/295d753e095c6ac8b180691f20d64ea8.png~tplv-obj.webp");
        Add("Team Bracelet", 2, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/54cb1eeca369e5bea1b97707ca05d189.png~tplv-obj.webp");
        Add("Finger Heart", 5, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/a4c4dc437fd3a6632aba149769491f49.png~tplv-obj.webp");
        Add("Overreact", 5, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/dfd48ef1952b6d315856adda7705d02d.png~tplv-obj.webp");
        Add("Name shoutout", 5, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/9b1d432109c95e77e8de11dd442c0a1f.png~tplv-obj.webp");
        Add("Duit Raya", 5, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/af543ee556c1c3d3e2610a24d8d02c94~tplv-obj.webp");
        Add("Pomegranate", 5, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/3940ea39b5180e353c49c5ebe207289c~tplv-obj.webp");
        Add("Gifts of Nowruz", 5, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/50ce02c7152a849dc83cdb06298f6c6a~tplv-obj.webp");
        Add("Embroidered Heart", 5, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/1f51d82f4713da5d9103ba34c8357782.png~tplv-obj.webp");
        Add("Cheer You Up", 9, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/97e0529ab9e5cbb60d95fc9ff1133ea6~tplv-obj.webp");
        Add("Club Power", 9, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/fb8da877eabca4ae295483f7cdfe7d31.png~tplv-obj.webp");
        Add("Super Popular", 9, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/2fa794a99919386b85402d9a0a991b2b.png~tplv-obj.webp");
        Add("Rosa", 10, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/eb77ead5c3abb6da6034d3cf6cfeb438~tplv-obj.webp");
        Add("Friendship Necklace", 10, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/e033c3f28632e233bebac1668ff66a2f.png~tplv-obj.webp");
        Add("Slow motion", 10, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/12374117770f779919bf002461fdfac0.png~tplv-obj.webp");
        Add("Chocolate", 10, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/8e8bfebfad922eed81f4a31a114fc0d3.png~tplv-obj.webp");
        Add("Heart Gaze", 10, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/0fe120fdb52724dd157e41cc5c00a924.png~tplv-obj.webp");
        Add("Perfume", 20, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/20b8f61246c7b6032777bb81bf4ee055~tplv-obj.webp");
        Add("Doughnut", 30, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/4e7ad6bdf0a1d860c538f38026d4e812~tplv-obj.webp");
        Add("Butterfly", 88, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/b066a6dfb1540ae0157965fb9462d0e6.png~tplv-obj.webp");
        Add("Paper Crane", 99, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/0f158a08f7886189cdabf496e8a07c21~tplv-obj.webp");
        Add("Little Crown", 99, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/cf3db11b94a975417043b53401d0afe1~tplv-obj.webp");
        Add("Cap", 99, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/6c2ab2da19249ea570a2ece5e3377f04~tplv-obj.webp");
        Add("Hat and Mustache", 99, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/2f1e4f3f5c728ffbfa35705b480fdc92~tplv-obj.webp");
        Add("Like-Pop", 99, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/75eb7b4aca24eaa6e566b566c7d21e2f~tplv-obj.webp");
        Add("Bubble Gum", 99, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/52ebbe9f3f53b5567ad11ad6f8303c58.png~tplv-obj.webp");
        Add("Game Controller", 100, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/20ec0eb50d82c2c445cb8391fd9fe6e2~tplv-obj.webp");
        Add("Super GG", 100, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/cbd7588c53ec3df1af0ed6d041566362.png~tplv-obj.webp");
        Add("Confetti", 100, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/cb4e11b3834e149f08e1cdcc93870b26~tplv-obj.webp");
        Add("Hand Hearts", 100, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/6cd022271dc4669d182cad856384870f~tplv-obj.webp");
        Add("Marvelous Confetti", 100, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/fccc851d351716bc8b34ec65786c727d~tplv-obj.webp");
        Add("Singing Magic", 100, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/1b76de4373dec56480903c3d5367fd13.png~tplv-obj.webp");
        Add("Bowknot", 149, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/resource/dd02c4c2cb726134314e89abec0b5476.png~tplv-obj.webp");
        Add("Sunglasses", 199, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/08af67ab13a8053269bf539fd27f3873.png~tplv-obj.webp");
        Add("Hearts", 199, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/934b5a10dee8376df5870a61d2ea5cb6.png~tplv-obj.webp");
        Add("Love You", 199, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/134e51c00f46e01976399883ca4e4798~tplv-obj.webp");
        Add("Cheer For You", 199, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/1059dfa76c78dc17d7cf0a1fc2ece185~tplv-obj.webp");
        Add("Corgi", 299, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/148eef0884fdb12058d1c6897d1e02b9~tplv-obj.webp");
        Add("Boxing Gloves", 299, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/9f8bd92363c400c284179f6719b6ba9c~tplv-obj.webp");
        Add("Forever Rosa", 399, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/863e7947bc793f694acbe970d70440a1.png~tplv-obj.webp");
        Add("Coral", 499, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/d4faa402c32bf4f92bee654b2663d9f1~tplv-obj.webp");
        Add("Panda Hug", 499, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/6a00e64d9582d0e1f4ef0ac66132c272.png~tplv-obj.webp");
        Add("Hands Up", 499, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/f4d906542408e6c87cf0a42f7426f0c6~tplv-obj.webp");
        Add("Money Gun", 500, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/e0589e95a2b41970f0f30f6202f5fce6~tplv-obj.webp");
        Add("Gem Gun", 500, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/dd06007ade737f1001977590b11d3f61~tplv-obj.webp");
        Add("Swan", 699, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/97a26919dbf6afe262c97e22a83f4bf1~tplv-obj.webp");
        Add("Train", 899, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/4227ed71f2c494b554f9cbe2147d4899~tplv-obj.webp");
        Add("Travel with You", 999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/753098e5a8f45afa965b73616c04cf89~tplv-obj.webp");
        Add("Lucky Airdrop Box", 999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/6ae56f08ae3ee57ea2dda0025bfd39d3.png~tplv-obj.webp");
        Add("Watermelon Love", 1000, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/1d1650cd9bb0e39d72a6e759525ffe59~tplv-obj.webp");
        Add("Galaxy", 1000, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/79a02148079526539f7599150da9fd28.png~tplv-obj.webp");
        Add("Fireworks", 1088, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/9494c8a0bc5c03521ef65368e59cc2b8~tplv-obj.webp");
        Add("Chasing the Dream", 1500, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/1ea8dbb805466c4ced19f29e9590040f~tplv-obj.webp");
        Add("Lover's Lock", 1500, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/f3010d1fcb008ce1b17248e5ea18b178.png~tplv-obj.webp");
        Add("Here We Go", 1799, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/61b76a51a3757f0ff1cdc33b16c4d8ae~tplv-obj.webp");
        Add("Love Drop", 1800, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/1ea684b3104abb725491a509022f7c02~tplv-obj.webp");
        Add("Star of Red Carpet", 1999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/5b9bf90278f87b9ca0c286d3c8a12936~tplv-obj.webp");
        Add("Cooper Flies Home", 1999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/3f1945b0d96e665a759f747e5e0cf7a9~tplv-obj.webp");
        Add("Whale Diving", 2150, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/46fa70966d8e931497f5289060f9a794~tplv-obj.webp");
        Add("Motorcycle", 2988, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/motor_icon_green.png~tplv-obj.webp");
        Add("Meteor Shower", 3000, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/71883933511237f7eaa1bf8cd12ed575~tplv-obj.webp");
        Add("Diamond Gun", 5000, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/651e705c26b704d03bc9c06d841808f1.png~tplv-obj.webp");
        Add("Unicorn Fantasy", 5000, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/resource/d040c8f602634506b4146cae6085b045.png~tplv-obj.webp");
        Add("Sports Car", 7000, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/e7ce188da898772f18aaffe49a7bd7db~tplv-obj.webp");
        Add("Leon the Kitten", 4888, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/a7748baba012c9e2d98a30dce7cc5a27~tplv-obj.webp");
        Add("Private Jet", 4888, "https://p16-webcast.tiktokcdn.com/img/alisg/webcast-sg/airplane_icon_gold.png~tplv-obj.webp");
        Add("Celebration Time", 6999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/e73e786041d8218d8e9dbbc150855f1b~tplv-obj.webp");
        Add("Happy Party", 6999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/41774a8ba83c59055e5f2946d51215b4~tplv-obj.webp");
        Add("Star Throne", 7999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/30063f6bc45aecc575c49ff3dbc33831~tplv-obj.webp");
        Add("Interstellar", 10000, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/8520d47b59c202a4534c1560a355ae06~tplv-obj.webp");
        Add("Sunset Speedway", 10000, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/df63eee488dc0994f6f5cb2e65f2ae49~tplv-obj.webp");
        Add("Red Lightning", 12000, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/5f48599c8d2a7bbc6e6fcf11ba2c809f~tplv-obj.webp");
        Add("Phoenix", 25999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/ef248375c4167d70c1642731c732c982~tplv-obj.webp");
        Add("Dragon Flame", 26999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/89b4d1d93c1cc614e3a0903ac7a94e0c~tplv-obj.webp");
        Add("Lion", 29999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/4fb89af2082a290b37d704e20f4fe729~tplv-obj.webp");
        Add("TikTok Universe", 44999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/8f471afbcebfda3841a6cc515e381f58~tplv-obj.webp");
        Add("TikTok Stars", 39999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/b1667c891ed39fd68ba7252fff7a1e7c~tplv-obj.webp");
        Add("Thunder Falcon", 39999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/26f3fbcda383e6093a19b8e7351a164c~tplv-obj.webp");
        Add("Castle Fantasy", 20000, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/8173e9b07875cca37caa5219e4903a40~tplv-obj.webp");
        Add("Fly Love", 19999, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/a598ba4c7024f4d46c1268be4d82f901~tplv-obj.webp");
        Add("Signature Jet", 4888, "https://p16-webcast.tiktokcdn.com/img/maliva/webcast-va/fe27eba54a50c0a687e3dc0f2c02067d~tplv-obj.webp");

        // Deduplicate by name (keep first occurrence)
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var deduped = new List<TikTokGift>();
        foreach (var g in gifts)
        {
            if (seen.Add(g.Name))
                deduped.Add(g);
        }

        return deduped.OrderBy(g => g.CoinPrice).ThenBy(g => g.Name).ToList();
    }
}
