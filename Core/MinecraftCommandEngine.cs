using System;
using System.Collections.Generic;
using System.Linq;

namespace ArcanePlayConnect.Core;

/// <summary>
/// Minecraft Java Edition command engine providing intelligent autocomplete suggestions.
/// Covers all major commands, entity types, effects, enchantments, items, blocks, gamemodes, etc.
/// </summary>
public static class MinecraftCommandEngine
{
    // ── Argument type identifiers ───────────────────────────────────────────
    private enum ArgType
    {
        Literal,        // Fixed keyword choices
        Selector,       // @a, @p, @r, @s, @e or player name
        Entity,         // minecraft:zombie, etc.
        Item,           // minecraft:diamond_sword, etc.
        Block,          // minecraft:stone, etc.
        Effect,         // minecraft:speed, etc.
        Enchantment,    // minecraft:sharpness, etc.
        Gamemode,       // survival, creative, etc.
        Difficulty,     // peaceful, easy, etc.
        Integer,        // any integer value
        Float,          // any float value
        Coordinate,     // ~ ~1 ~-2 or ^ ^ ^2 or 100 64 200  (consumes 3 tokens)
        Boolean,        // true/false
        Message,        // free-form text (consumes all remaining tokens)
        Nbt,            // NBT compound data
        BiomeId,        // biome identifiers
        Dimension,      // dimension identifiers
        Particle,       // particle types
        Attribute,      // generic.max_health etc.
        Slot,           // equipment slots
        Time,           // day/night/noon/midnight or tick values
        Sound,          // sound event identifiers
        Structure,      // structure identifiers
        Gamerule,       // game rule names
        Color,          // team colors
        ScoreboardOp,   // scoreboard operations
        Advancement,    // advancement identifiers
        FreeForm,       // anything typed by user (consumes all remaining tokens)
    }

    /// <summary>How many space-separated tokens an argument type consumes.</summary>
    private static int TokenWidth(ArgType type) => type switch
    {
        ArgType.Coordinate => 3,
        ArgType.Message    => int.MaxValue, // greedy
        ArgType.FreeForm   => int.MaxValue, // greedy
        _                  => 1
    };

    private sealed class CommandArg
    {
        public ArgType Type { get; init; }
        public string Hint { get; init; } = "";
        public string[]? Choices { get; init; }
        public bool Optional { get; init; }
    }

    private sealed class CommandDef
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public List<List<CommandArg>> Overloads { get; init; } = new();
    }

    // ── Lookup tables ───────────────────────────────────────────────────────

    private static readonly string[] Selectors = { "@a", "@p", "@r", "@s", "@e", "{nickname}", "{username}" };

    private static readonly string[] SelectorParameters =
    {
        "type=", "r=", "rm=", "x=", "y=", "z=", "dx=", "dy=", "dz=",
        "scores=", "tag=", "team=", "name=", "limit=", "sort=",
        "level=", "gamemode=", "nbt=", "distance=", "x_rotation=", "y_rotation="
    };

    private static readonly string[] Gamemodes = { "survival", "creative", "adventure", "spectator" };
    private static readonly string[] Difficulties = { "peaceful", "easy", "normal", "hard" };
    private static readonly string[] Booleans = { "true", "false" };
    private static readonly string[] TimePresets = { "day", "night", "noon", "midnight", "0", "1000", "6000", "12000", "13000", "18000" };
    private static readonly string[] Colors =
    {
        "black", "dark_blue", "dark_green", "dark_aqua", "dark_red", "dark_purple",
        "gold", "gray", "dark_gray", "blue", "green", "aqua", "red",
        "light_purple", "yellow", "white"
    };

    private static readonly string[] Slots =
    {
        "mainhand", "offhand", "head", "chest", "legs", "feet",
        "armor.head", "armor.chest", "armor.legs", "armor.feet",
        "weapon.mainhand", "weapon.offhand",
        "container.0", "container.1", "container.2", "hotbar.0", "hotbar.1", "hotbar.2",
        "enderchest.0", "inventory.0"
    };

    private static readonly string[] ScoreboardOperations = { "+=", "-=", "*=", "/=", "%=", "=", "<", ">", "><" };

    private static readonly string[] Entities =
    {
        "minecraft:allay", "minecraft:area_effect_cloud", "minecraft:armadillo", "minecraft:armor_stand",
        "minecraft:arrow", "minecraft:axolotl", "minecraft:bat", "minecraft:bee",
        "minecraft:blaze", "minecraft:block_display", "minecraft:boat", "minecraft:bogged",
        "minecraft:breeze", "minecraft:camel", "minecraft:cat", "minecraft:cave_spider",
        "minecraft:chest_boat", "minecraft:chest_minecart", "minecraft:chicken",
        "minecraft:cod", "minecraft:command_block_minecart", "minecraft:cow", "minecraft:creaking",
        "minecraft:creeper", "minecraft:dolphin", "minecraft:donkey",
        "minecraft:dragon_fireball", "minecraft:drowned", "minecraft:egg",
        "minecraft:elder_guardian", "minecraft:end_crystal", "minecraft:ender_dragon",
        "minecraft:ender_pearl", "minecraft:enderman", "minecraft:endermite",
        "minecraft:evoker", "minecraft:evoker_fangs", "minecraft:experience_bottle",
        "minecraft:experience_orb", "minecraft:eye_of_ender", "minecraft:falling_block",
        "minecraft:fireball", "minecraft:firework_rocket", "minecraft:fishing_bobber",
        "minecraft:fox", "minecraft:frog", "minecraft:furnace_minecart",
        "minecraft:ghast", "minecraft:giant", "minecraft:glow_item_frame",
        "minecraft:glow_squid", "minecraft:goat", "minecraft:guardian",
        "minecraft:hoglin", "minecraft:hopper_minecart", "minecraft:horse",
        "minecraft:husk", "minecraft:illusioner", "minecraft:interaction",
        "minecraft:iron_golem", "minecraft:item", "minecraft:item_display",
        "minecraft:item_frame", "minecraft:leash_knot", "minecraft:lightning_bolt",
        "minecraft:llama", "minecraft:llama_spit", "minecraft:magma_cube",
        "minecraft:marker", "minecraft:minecart", "minecraft:mooshroom",
        "minecraft:mule", "minecraft:ocelot", "minecraft:painting",
        "minecraft:panda", "minecraft:parrot", "minecraft:phantom",
        "minecraft:pig", "minecraft:piglin", "minecraft:piglin_brute",
        "minecraft:pillager", "minecraft:player", "minecraft:polar_bear",
        "minecraft:potion", "minecraft:pufferfish", "minecraft:rabbit",
        "minecraft:ravager", "minecraft:salmon", "minecraft:sheep",
        "minecraft:shulker", "minecraft:shulker_bullet", "minecraft:silverfish",
        "minecraft:skeleton", "minecraft:skeleton_horse", "minecraft:slime",
        "minecraft:small_fireball", "minecraft:sniffer", "minecraft:snow_golem",
        "minecraft:snowball", "minecraft:spawner_minecart", "minecraft:spectral_arrow",
        "minecraft:spider", "minecraft:squid", "minecraft:stray",
        "minecraft:strider", "minecraft:tadpole", "minecraft:text_display",
        "minecraft:tnt", "minecraft:tnt_minecart", "minecraft:trader_llama",
        "minecraft:trident", "minecraft:tropical_fish", "minecraft:turtle",
        "minecraft:vex", "minecraft:villager", "minecraft:vindicator",
        "minecraft:wandering_trader", "minecraft:warden", "minecraft:wind_charge",
        "minecraft:witch", "minecraft:wither", "minecraft:wither_skeleton",
        "minecraft:wither_skull", "minecraft:wolf", "minecraft:zoglin",
        "minecraft:zombie", "minecraft:zombie_horse", "minecraft:zombie_villager",
        "minecraft:zombified_piglin"
    };

    private static readonly string[] Effects =
    {
        "minecraft:absorption", "minecraft:bad_omen", "minecraft:blindness",
        "minecraft:conduit_power", "minecraft:darkness", "minecraft:dolphins_grace",
        "minecraft:fire_resistance", "minecraft:glowing", "minecraft:haste",
        "minecraft:health_boost", "minecraft:hero_of_the_village", "minecraft:hunger",
        "minecraft:infested", "minecraft:instant_damage", "minecraft:instant_health",
        "minecraft:invisibility", "minecraft:jump_boost", "minecraft:levitation",
        "minecraft:luck", "minecraft:mining_fatigue", "minecraft:nausea",
        "minecraft:night_vision", "minecraft:oozing", "minecraft:poison",
        "minecraft:raid_omen", "minecraft:regeneration", "minecraft:resistance",
        "minecraft:saturation", "minecraft:slow_falling", "minecraft:slowness",
        "minecraft:speed", "minecraft:strength", "minecraft:trial_omen",
        "minecraft:unluck", "minecraft:water_breathing", "minecraft:weakness",
        "minecraft:weaving", "minecraft:wind_charged", "minecraft:wither"
    };

    private static readonly string[] Enchantments =
    {
        "minecraft:aqua_affinity", "minecraft:bane_of_arthropods", "minecraft:binding_curse",
        "minecraft:blast_protection", "minecraft:breach", "minecraft:channeling",
        "minecraft:density", "minecraft:depth_strider", "minecraft:efficiency",
        "minecraft:feather_falling", "minecraft:fire_aspect", "minecraft:fire_protection",
        "minecraft:flame", "minecraft:fortune", "minecraft:frost_walker",
        "minecraft:impaling", "minecraft:infinity", "minecraft:knockback",
        "minecraft:looting", "minecraft:loyalty", "minecraft:luck_of_the_sea",
        "minecraft:lure", "minecraft:mending", "minecraft:multishot",
        "minecraft:piercing", "minecraft:power", "minecraft:projectile_protection",
        "minecraft:protection", "minecraft:punch", "minecraft:quick_charge",
        "minecraft:respiration", "minecraft:riptide", "minecraft:sharpness",
        "minecraft:silk_touch", "minecraft:smite", "minecraft:soul_speed",
        "minecraft:sweeping_edge", "minecraft:swift_sneak", "minecraft:thorns",
        "minecraft:unbreaking", "minecraft:vanishing_curse", "minecraft:wind_burst"
    };

    private static readonly string[] Items =
    {
        "minecraft:diamond_sword", "minecraft:diamond_pickaxe", "minecraft:diamond_axe",
        "minecraft:diamond_shovel", "minecraft:diamond_hoe", "minecraft:diamond_helmet",
        "minecraft:diamond_chestplate", "minecraft:diamond_leggings", "minecraft:diamond_boots",
        "minecraft:netherite_sword", "minecraft:netherite_pickaxe", "minecraft:netherite_axe",
        "minecraft:netherite_shovel", "minecraft:netherite_hoe", "minecraft:netherite_helmet",
        "minecraft:netherite_chestplate", "minecraft:netherite_leggings", "minecraft:netherite_boots",
        "minecraft:iron_sword", "minecraft:iron_pickaxe", "minecraft:iron_axe",
        "minecraft:iron_shovel", "minecraft:iron_hoe", "minecraft:iron_helmet",
        "minecraft:iron_chestplate", "minecraft:iron_leggings", "minecraft:iron_boots",
        "minecraft:golden_sword", "minecraft:golden_pickaxe", "minecraft:golden_axe",
        "minecraft:golden_shovel", "minecraft:golden_hoe", "minecraft:golden_helmet",
        "minecraft:golden_chestplate", "minecraft:golden_leggings", "minecraft:golden_boots",
        "minecraft:stone_sword", "minecraft:stone_pickaxe", "minecraft:stone_axe",
        "minecraft:stone_shovel", "minecraft:stone_hoe",
        "minecraft:wooden_sword", "minecraft:wooden_pickaxe", "minecraft:wooden_axe",
        "minecraft:wooden_shovel", "minecraft:wooden_hoe",
        "minecraft:bow", "minecraft:crossbow", "minecraft:trident", "minecraft:mace",
        "minecraft:shield", "minecraft:arrow", "minecraft:spectral_arrow", "minecraft:tipped_arrow",
        "minecraft:fishing_rod", "minecraft:flint_and_steel", "minecraft:shears",
        "minecraft:compass", "minecraft:clock", "minecraft:recovery_compass",
        "minecraft:spyglass", "minecraft:map", "minecraft:filled_map",
        "minecraft:elytra", "minecraft:totem_of_undying", "minecraft:name_tag",
        "minecraft:lead", "minecraft:saddle", "minecraft:ender_pearl",
        "minecraft:ender_eye", "minecraft:firework_rocket", "minecraft:firework_star",
        "minecraft:apple", "minecraft:golden_apple", "minecraft:enchanted_golden_apple",
        "minecraft:bread", "minecraft:cooked_beef", "minecraft:cooked_porkchop",
        "minecraft:cooked_chicken", "minecraft:cooked_mutton", "minecraft:cooked_salmon",
        "minecraft:cooked_cod", "minecraft:cookie", "minecraft:cake",
        "minecraft:pumpkin_pie", "minecraft:sweet_berries", "minecraft:glow_berries",
        "minecraft:potion", "minecraft:splash_potion", "minecraft:lingering_potion",
        "minecraft:experience_bottle", "minecraft:milk_bucket",
        "minecraft:diamond", "minecraft:emerald", "minecraft:gold_ingot", "minecraft:iron_ingot",
        "minecraft:netherite_ingot", "minecraft:copper_ingot", "minecraft:lapis_lazuli",
        "minecraft:redstone", "minecraft:coal", "minecraft:charcoal",
        "minecraft:quartz", "minecraft:amethyst_shard", "minecraft:echo_shard",
        "minecraft:glowstone_dust", "minecraft:gunpowder", "minecraft:blaze_rod",
        "minecraft:blaze_powder", "minecraft:ender_pearl", "minecraft:ghast_tear",
        "minecraft:nether_star", "minecraft:phantom_membrane", "minecraft:rabbit_hide",
        "minecraft:leather", "minecraft:string", "minecraft:slime_ball",
        "minecraft:bone", "minecraft:bone_meal", "minecraft:feather",
        "minecraft:stick", "minecraft:flint", "minecraft:paper",
        "minecraft:book", "minecraft:enchanted_book", "minecraft:written_book",
        "minecraft:writable_book", "minecraft:knowledge_book",
        "minecraft:bucket", "minecraft:water_bucket", "minecraft:lava_bucket",
        "minecraft:powder_snow_bucket", "minecraft:axolotl_bucket",
        "minecraft:spawn_egg", "minecraft:command_block",
        "minecraft:structure_block", "minecraft:barrier", "minecraft:light",
        "minecraft:debug_stick", "minecraft:command_block_minecart",
        "minecraft:tnt", "minecraft:torch", "minecraft:lantern",
        "minecraft:campfire", "minecraft:chest", "minecraft:ender_chest",
        "minecraft:shulker_box", "minecraft:crafting_table", "minecraft:furnace",
        "minecraft:anvil", "minecraft:enchanting_table", "minecraft:brewing_stand",
        "minecraft:hopper", "minecraft:dispenser", "minecraft:dropper",
        "minecraft:observer", "minecraft:piston", "minecraft:sticky_piston",
        "minecraft:redstone_block", "minecraft:repeater", "minecraft:comparator",
        "minecraft:daylight_detector", "minecraft:tripwire_hook", "minecraft:lever",
        "minecraft:stone_button", "minecraft:oak_button",
        "minecraft:oak_planks", "minecraft:spruce_planks", "minecraft:birch_planks",
        "minecraft:jungle_planks", "minecraft:acacia_planks", "minecraft:dark_oak_planks",
        "minecraft:mangrove_planks", "minecraft:cherry_planks", "minecraft:bamboo_planks",
        "minecraft:crimson_planks", "minecraft:warped_planks",
        "minecraft:glass", "minecraft:glass_pane",
        "minecraft:cobblestone", "minecraft:stone", "minecraft:deepslate",
        "minecraft:granite", "minecraft:diorite", "minecraft:andesite",
        "minecraft:dirt", "minecraft:grass_block", "minecraft:sand",
        "minecraft:gravel", "minecraft:clay", "minecraft:obsidian",
        "minecraft:bedrock", "minecraft:netherrack", "minecraft:end_stone",
        "minecraft:sponge", "minecraft:wet_sponge",
        "minecraft:white_wool", "minecraft:orange_wool", "minecraft:magenta_wool",
        "minecraft:light_blue_wool", "minecraft:yellow_wool", "minecraft:lime_wool",
        "minecraft:pink_wool", "minecraft:gray_wool", "minecraft:light_gray_wool",
        "minecraft:cyan_wool", "minecraft:purple_wool", "minecraft:blue_wool",
        "minecraft:brown_wool", "minecraft:green_wool", "minecraft:red_wool",
        "minecraft:black_wool",
        "minecraft:white_concrete", "minecraft:orange_concrete", "minecraft:magenta_concrete",
        "minecraft:light_blue_concrete", "minecraft:yellow_concrete", "minecraft:lime_concrete",
        "minecraft:pink_concrete", "minecraft:gray_concrete",
        "minecraft:oak_log", "minecraft:spruce_log", "minecraft:birch_log",
        "minecraft:jungle_log", "minecraft:acacia_log", "minecraft:dark_oak_log",
        "minecraft:mangrove_log", "minecraft:cherry_log", "minecraft:bamboo_block",
        "minecraft:crimson_stem", "minecraft:warped_stem"
    };

    private static readonly string[] Blocks =
    {
        "minecraft:air", "minecraft:stone", "minecraft:granite", "minecraft:polished_granite",
        "minecraft:diorite", "minecraft:polished_diorite", "minecraft:andesite",
        "minecraft:polished_andesite", "minecraft:grass_block", "minecraft:dirt",
        "minecraft:coarse_dirt", "minecraft:podzol", "minecraft:cobblestone",
        "minecraft:oak_planks", "minecraft:spruce_planks", "minecraft:birch_planks",
        "minecraft:jungle_planks", "minecraft:acacia_planks", "minecraft:dark_oak_planks",
        "minecraft:mangrove_planks", "minecraft:cherry_planks", "minecraft:bamboo_planks",
        "minecraft:crimson_planks", "minecraft:warped_planks",
        "minecraft:bedrock", "minecraft:water", "minecraft:lava",
        "minecraft:sand", "minecraft:red_sand", "minecraft:gravel",
        "minecraft:gold_ore", "minecraft:deepslate_gold_ore",
        "minecraft:iron_ore", "minecraft:deepslate_iron_ore",
        "minecraft:coal_ore", "minecraft:deepslate_coal_ore",
        "minecraft:diamond_ore", "minecraft:deepslate_diamond_ore",
        "minecraft:emerald_ore", "minecraft:deepslate_emerald_ore",
        "minecraft:lapis_ore", "minecraft:deepslate_lapis_ore",
        "minecraft:redstone_ore", "minecraft:deepslate_redstone_ore",
        "minecraft:copper_ore", "minecraft:deepslate_copper_ore",
        "minecraft:nether_gold_ore", "minecraft:nether_quartz_ore",
        "minecraft:ancient_debris",
        "minecraft:oak_log", "minecraft:spruce_log", "minecraft:birch_log",
        "minecraft:jungle_log", "minecraft:acacia_log", "minecraft:dark_oak_log",
        "minecraft:mangrove_log", "minecraft:cherry_log",
        "minecraft:glass", "minecraft:tinted_glass",
        "minecraft:sponge", "minecraft:wet_sponge",
        "minecraft:tnt", "minecraft:obsidian", "minecraft:crying_obsidian",
        "minecraft:glowstone", "minecraft:sea_lantern", "minecraft:shroomlight",
        "minecraft:torch", "minecraft:wall_torch", "minecraft:soul_torch",
        "minecraft:lantern", "minecraft:soul_lantern",
        "minecraft:chest", "minecraft:ender_chest", "minecraft:barrel",
        "minecraft:crafting_table", "minecraft:furnace", "minecraft:blast_furnace",
        "minecraft:smoker", "minecraft:brewing_stand", "minecraft:anvil",
        "minecraft:enchanting_table",
        "minecraft:command_block", "minecraft:chain_command_block", "minecraft:repeating_command_block",
        "minecraft:structure_block", "minecraft:jigsaw", "minecraft:barrier", "minecraft:light",
        "minecraft:spawner", "minecraft:trial_spawner",
        "minecraft:white_wool", "minecraft:white_concrete", "minecraft:white_terracotta",
        "minecraft:white_stained_glass",
        "minecraft:redstone_block", "minecraft:piston", "minecraft:sticky_piston",
        "minecraft:slime_block", "minecraft:honey_block",
        "minecraft:note_block", "minecraft:jukebox", "minecraft:bell",
        "minecraft:end_portal_frame", "minecraft:end_gateway",
        "minecraft:dragon_egg", "minecraft:beacon", "minecraft:conduit",
        "minecraft:respawn_anchor", "minecraft:lodestone"
    };

    private static readonly string[] Particles =
    {
        "minecraft:ambient_entity_effect", "minecraft:angry_villager", "minecraft:ash",
        "minecraft:block", "minecraft:block_marker", "minecraft:bubble",
        "minecraft:bubble_column_up", "minecraft:bubble_pop", "minecraft:campfire_cosy_smoke",
        "minecraft:campfire_signal_smoke", "minecraft:cherry_leaves", "minecraft:cloud",
        "minecraft:composter", "minecraft:crimson_spore", "minecraft:crit",
        "minecraft:current_down", "minecraft:damage_indicator", "minecraft:dolphin",
        "minecraft:dragon_breath", "minecraft:dripping_dripstone_lava",
        "minecraft:dripping_dripstone_water", "minecraft:dripping_honey",
        "minecraft:dripping_lava", "minecraft:dripping_obsidian_tear",
        "minecraft:dripping_water", "minecraft:dust", "minecraft:dust_color_transition",
        "minecraft:dust_plume", "minecraft:effect", "minecraft:egg_crack",
        "minecraft:elder_guardian", "minecraft:electric_spark", "minecraft:enchant",
        "minecraft:enchanted_hit", "minecraft:end_rod", "minecraft:entity_effect",
        "minecraft:explosion", "minecraft:explosion_emitter", "minecraft:falling_dripstone_lava",
        "minecraft:falling_dripstone_water", "minecraft:falling_dust",
        "minecraft:falling_honey", "minecraft:falling_lava", "minecraft:falling_nectar",
        "minecraft:falling_obsidian_tear", "minecraft:falling_spore_blossom",
        "minecraft:falling_water", "minecraft:firework", "minecraft:fishing",
        "minecraft:flame", "minecraft:flash", "minecraft:glow",
        "minecraft:glow_squid_ink", "minecraft:gust", "minecraft:gust_emitter_large",
        "minecraft:gust_emitter_small", "minecraft:happy_villager",
        "minecraft:heart", "minecraft:infested", "minecraft:instant_effect",
        "minecraft:item", "minecraft:item_cobweb", "minecraft:item_slime",
        "minecraft:item_snowball", "minecraft:landing_honey",
        "minecraft:landing_lava", "minecraft:landing_obsidian_tear",
        "minecraft:large_smoke", "minecraft:lava", "minecraft:mycelium",
        "minecraft:nautilus", "minecraft:note", "minecraft:ominous_spawning",
        "minecraft:poof", "minecraft:portal", "minecraft:raid_omen",
        "minecraft:rain", "minecraft:reverse_portal", "minecraft:scrape",
        "minecraft:sculk_charge", "minecraft:sculk_charge_pop", "minecraft:sculk_soul",
        "minecraft:shriek", "minecraft:small_flame", "minecraft:small_gust",
        "minecraft:smoke", "minecraft:sneeze", "minecraft:snowflake",
        "minecraft:sonic_boom", "minecraft:soul", "minecraft:soul_fire_flame",
        "minecraft:spit", "minecraft:splash", "minecraft:spore_blossom_air",
        "minecraft:squid_ink", "minecraft:sweep_attack", "minecraft:totem_of_undying",
        "minecraft:trail", "minecraft:trial_omen", "minecraft:trial_spawner_detection",
        "minecraft:trial_spawner_detection_ominous", "minecraft:underwater",
        "minecraft:vault_connection", "minecraft:vibration",
        "minecraft:warped_spore", "minecraft:wax_off", "minecraft:wax_on",
        "minecraft:white_ash", "minecraft:white_smoke", "minecraft:witch"
    };

    private static readonly string[] Biomes =
    {
        "minecraft:badlands", "minecraft:bamboo_jungle", "minecraft:basalt_deltas",
        "minecraft:beach", "minecraft:birch_forest", "minecraft:cherry_grove",
        "minecraft:cold_ocean", "minecraft:crimson_forest", "minecraft:dark_forest",
        "minecraft:deep_cold_ocean", "minecraft:deep_dark", "minecraft:deep_frozen_ocean",
        "minecraft:deep_lukewarm_ocean", "minecraft:deep_ocean",
        "minecraft:desert", "minecraft:dripstone_caves", "minecraft:end_barrens",
        "minecraft:end_highlands", "minecraft:end_midlands",
        "minecraft:eroded_badlands", "minecraft:flower_forest", "minecraft:forest",
        "minecraft:frozen_ocean", "minecraft:frozen_peaks", "minecraft:frozen_river",
        "minecraft:grove", "minecraft:ice_spikes", "minecraft:jagged_peaks",
        "minecraft:jungle", "minecraft:lukewarm_ocean", "minecraft:lush_caves",
        "minecraft:mangrove_swamp", "minecraft:meadow", "minecraft:mushroom_fields",
        "minecraft:nether_wastes", "minecraft:ocean", "minecraft:old_growth_birch_forest",
        "minecraft:old_growth_pine_taiga", "minecraft:old_growth_spruce_taiga",
        "minecraft:plains", "minecraft:river", "minecraft:savanna",
        "minecraft:savanna_plateau", "minecraft:small_end_islands",
        "minecraft:snowy_beach", "minecraft:snowy_plains", "minecraft:snowy_slopes",
        "minecraft:snowy_taiga", "minecraft:soul_sand_valley",
        "minecraft:sparse_jungle", "minecraft:stony_peaks", "minecraft:stony_shore",
        "minecraft:sunflower_plains", "minecraft:swamp", "minecraft:taiga",
        "minecraft:the_end", "minecraft:the_void", "minecraft:warm_ocean",
        "minecraft:warped_forest", "minecraft:windswept_forest",
        "minecraft:windswept_gravelly_hills", "minecraft:windswept_hills",
        "minecraft:windswept_savanna", "minecraft:wooded_badlands"
    };

    private static readonly string[] Dimensions =
    {
        "minecraft:overworld", "minecraft:the_nether", "minecraft:the_end"
    };

    private static readonly string[] Gamerules =
    {
        "announceAdvancements", "blockExplosionDropDecay", "commandBlockOutput",
        "commandModificationBlockLimit", "disableElytraMovementCheck",
        "disableRaids", "doDaylightCycle", "doEntityDrops", "doFireTick",
        "doImmediateRespawn", "doInsomnia", "doLimitedCrafting",
        "doMobLoot", "doMobSpawning", "doPatrolSpawning",
        "doTileDrops", "doTraderSpawning", "doVinesSpread",
        "doWardenSpawning", "doWeatherCycle", "drowningDamage",
        "enderPearlsVanishOnDeath", "fallDamage", "fireDamage",
        "forgiveDeadPlayers", "freezeDamage", "globalSoundEvents",
        "keepInventory", "lavaSourceConversion", "logAdminCommands",
        "maxCommandChainLength", "maxCommandForkCount", "maxEntityCramming",
        "mobExplosionDropDecay", "mobGriefing", "naturalRegeneration",
        "playersNetherPortalCreativeDelay", "playersNetherPortalDefaultDelay",
        "playersSleepingPercentage", "projectilesCanBreakBlocks",
        "randomTickSpeed", "reducedDebugInfo", "sendCommandFeedback",
        "showDeathMessages", "snowAccumulationHeight", "spawnChunkRadius",
        "spawnRadius", "spectatorsGenerateChunks", "tntExplosionDropDecay",
        "tntExplodes", "universalAnger", "waterSourceConversion"
    };

    private static readonly string[] Attributes =
    {
        "minecraft:generic.armor", "minecraft:generic.armor_toughness",
        "minecraft:generic.attack_damage", "minecraft:generic.attack_knockback",
        "minecraft:generic.attack_speed", "minecraft:generic.burning_time",
        "minecraft:generic.explosion_knockback_resistance", "minecraft:generic.fall_damage_multiplier",
        "minecraft:generic.flying_speed", "minecraft:generic.follow_range",
        "minecraft:generic.gravity", "minecraft:generic.jump_strength",
        "minecraft:generic.knockback_resistance", "minecraft:generic.luck",
        "minecraft:generic.max_absorption", "minecraft:generic.max_health",
        "minecraft:generic.movement_efficiency", "minecraft:generic.movement_speed",
        "minecraft:generic.oxygen_bonus", "minecraft:generic.safe_fall_distance",
        "minecraft:generic.scale", "minecraft:generic.step_height",
        "minecraft:generic.water_movement_efficiency",
        "minecraft:zombie.spawn_reinforcements"
    };

    private static readonly string[] Structures =
    {
        "minecraft:bastion_remnant", "minecraft:buried_treasure", "minecraft:desert_pyramid",
        "minecraft:end_city", "minecraft:fortress", "minecraft:igloo",
        "minecraft:jungle_pyramid", "minecraft:mansion", "minecraft:mineshaft",
        "minecraft:monument", "minecraft:ocean_ruin", "minecraft:pillager_outpost",
        "minecraft:ruined_portal", "minecraft:shipwreck", "minecraft:stronghold",
        "minecraft:swamp_hut", "minecraft:trail_ruins", "minecraft:trial_chambers",
        "minecraft:village", "minecraft:ancient_city"
    };

    // ── Command definitions ─────────────────────────────────────────────────

    private static readonly List<CommandDef> Commands = BuildCommandDefinitions();

    private static List<CommandDef> BuildCommandDefinitions()
    {
        var sel = new CommandArg { Type = ArgType.Selector, Hint = "<target>" };
        var entity = new CommandArg { Type = ArgType.Entity, Hint = "<entity>" };
        var item = new CommandArg { Type = ArgType.Item, Hint = "<item>" };
        var block = new CommandArg { Type = ArgType.Block, Hint = "<block>" };
        var effect = new CommandArg { Type = ArgType.Effect, Hint = "<effect>" };
        var enchant = new CommandArg { Type = ArgType.Enchantment, Hint = "<enchantment>" };
        var pos = new CommandArg { Type = ArgType.Coordinate, Hint = "<x y z>" };
        var intArg = new CommandArg { Type = ArgType.Integer, Hint = "<amount>" };
        var floatArg = new CommandArg { Type = ArgType.Float, Hint = "<value>" };
        var boolArg = new CommandArg { Type = ArgType.Boolean, Hint = "<value>" };
        var msg = new CommandArg { Type = ArgType.Message, Hint = "<message...>" };
        var nbt = new CommandArg { Type = ArgType.Nbt, Hint = "<nbt>" };
        var free = new CommandArg { Type = ArgType.FreeForm, Hint = "" };

        return new List<CommandDef>
        {
            // ── Movement / teleport ──
            new()
            {
                Name = "tp",
                Description = "Teleport entities",
                Overloads = new()
                {
                    new() { sel, sel },
                    new() { sel, pos },
                    new() { pos }
                }
            },
            new()
            {
                Name = "teleport",
                Description = "Teleport entities",
                Overloads = new()
                {
                    new() { sel, sel },
                    new() { sel, pos },
                    new() { pos }
                }
            },
            new()
            {
                Name = "spreadplayers",
                Description = "Spread entities around a point",
                Overloads = new()
                {
                    new()
                    {
                        new() { Type = ArgType.Coordinate, Hint = "<x z>" },
                        new() { Type = ArgType.Float, Hint = "<spreadDistance>" },
                        new() { Type = ArgType.Float, Hint = "<maxRange>" },
                        boolArg,
                        sel
                    }
                }
            },

            // ── Chat / display ──
            new()
            {
                Name = "say",
                Description = "Send a message to all players",
                Overloads = new() { new() { msg } }
            },
            new()
            {
                Name = "msg",
                Description = "Send a private message",
                Overloads = new() { new() { sel, msg } }
            },
            new()
            {
                Name = "tell",
                Description = "Send a private message",
                Overloads = new() { new() { sel, msg } }
            },
            new()
            {
                Name = "w",
                Description = "Send a private message",
                Overloads = new() { new() { sel, msg } }
            },
            new()
            {
                Name = "me",
                Description = "Send an action message",
                Overloads = new() { new() { msg } }
            },
            new()
            {
                Name = "tellraw",
                Description = "Send a JSON message",
                Overloads = new() { new() { sel, free } }
            },
            new()
            {
                Name = "title",
                Description = "Display a title",
                Overloads = new()
                {
                    new() { sel, new() { Type = ArgType.Literal, Choices = new[] { "title", "subtitle", "actionbar" }, Hint = "<position>" }, free },
                    new() { sel, new() { Type = ArgType.Literal, Choices = new[] { "times" }, Hint = "times" }, intArg, intArg, intArg },
                    new() { sel, new() { Type = ArgType.Literal, Choices = new[] { "clear", "reset" }, Hint = "clear|reset" } }
                }
            },

            // ── Entity management ──
            new()
            {
                Name = "summon",
                Description = "Summon an entity",
                Overloads = new()
                {
                    new() { entity },
                    new() { entity, pos },
                    new() { entity, pos, nbt }
                }
            },
            new()
            {
                Name = "kill",
                Description = "Kill entities",
                Overloads = new()
                {
                    new() { sel },
                    new() { }
                }
            },
            new()
            {
                Name = "ride",
                Description = "Mount entities",
                Overloads = new()
                {
                    new() { sel, new() { Type = ArgType.Literal, Choices = new[] { "mount", "dismount" }, Hint = "mount|dismount" }, sel }
                }
            },
            new()
            {
                Name = "damage",
                Description = "Deal damage to entities",
                Overloads = new()
                {
                    new() { sel, floatArg, new() { Type = ArgType.Literal, Choices = new[] { "minecraft:generic", "minecraft:player_attack", "minecraft:mob_attack", "minecraft:arrow", "minecraft:explosion", "minecraft:fall", "minecraft:fire", "minecraft:lava", "minecraft:drown", "minecraft:lightning_bolt", "minecraft:wither", "minecraft:magic", "minecraft:starve", "minecraft:freeze" }, Hint = "<damageType>" } }
                }
            },

            // ── Items ──
            new()
            {
                Name = "give",
                Description = "Give items to players",
                Overloads = new()
                {
                    new() { sel, item },
                    new() { sel, item, intArg }
                }
            },
            new()
            {
                Name = "clear",
                Description = "Clear items from inventory",
                Overloads = new()
                {
                    new() { sel },
                    new() { sel, item },
                    new() { sel, item, intArg }
                }
            },
            new()
            {
                Name = "item",
                Description = "Manipulate items in inventories",
                Overloads = new()
                {
                    new()
                    {
                        new() { Type = ArgType.Literal, Choices = new[] { "replace", "modify" }, Hint = "replace|modify" },
                        new() { Type = ArgType.Literal, Choices = new[] { "entity", "block" }, Hint = "entity|block" },
                        sel,
                        new() { Type = ArgType.Slot, Hint = "<slot>" },
                        new() { Type = ArgType.Literal, Choices = new[] { "with" }, Hint = "with" },
                        item
                    }
                }
            },

            // ── Effects / enchanting ──
            new()
            {
                Name = "effect",
                Description = "Add or remove status effects",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "give" }, Hint = "give" }, sel, effect },
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "give" }, Hint = "give" }, sel, effect, intArg },
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "give" }, Hint = "give" }, sel, effect, intArg, intArg },
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "give" }, Hint = "give" }, sel, effect, intArg, intArg, boolArg },
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "clear" }, Hint = "clear" }, sel },
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "clear" }, Hint = "clear" }, sel, effect }
                }
            },
            new()
            {
                Name = "enchant",
                Description = "Enchant a player's item",
                Overloads = new()
                {
                    new() { sel, enchant },
                    new() { sel, enchant, intArg }
                }
            },

            // ── Blocks ──
            new()
            {
                Name = "setblock",
                Description = "Set a block at a position",
                Overloads = new()
                {
                    new() { pos, block },
                    new() { pos, block, new() { Type = ArgType.Literal, Choices = new[] { "destroy", "keep", "replace" }, Hint = "mode" } }
                }
            },
            new()
            {
                Name = "fill",
                Description = "Fill a region with blocks",
                Overloads = new()
                {
                    new() { pos, pos, block },
                    new() { pos, pos, block, new() { Type = ArgType.Literal, Choices = new[] { "destroy", "hollow", "keep", "outline", "replace" }, Hint = "mode" } }
                }
            },
            new()
            {
                Name = "clone",
                Description = "Clone blocks from one area to another",
                Overloads = new()
                {
                    new() { pos, pos, pos },
                    new() { pos, pos, pos, new() { Type = ArgType.Literal, Choices = new[] { "replace", "masked", "filtered" }, Hint = "mask" }, new() { Type = ArgType.Literal, Choices = new[] { "force", "move", "normal" }, Hint = "mode" } }
                }
            },

            // ── Game settings ──
            new()
            {
                Name = "gamemode",
                Description = "Set a player's game mode",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Gamemode, Hint = "<mode>" } },
                    new() { new() { Type = ArgType.Gamemode, Hint = "<mode>" }, sel }
                }
            },
            new()
            {
                Name = "difficulty",
                Description = "Set the game difficulty",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Difficulty, Hint = "<difficulty>" } }
                }
            },
            new()
            {
                Name = "gamerule",
                Description = "Set or query a game rule",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Gamerule, Hint = "<rule>" } },
                    new() { new() { Type = ArgType.Gamerule, Hint = "<rule>" }, free }
                }
            },
            new()
            {
                Name = "defaultgamemode",
                Description = "Set the default game mode",
                Overloads = new() { new() { new() { Type = ArgType.Gamemode, Hint = "<mode>" } } }
            },
            new()
            {
                Name = "weather",
                Description = "Set the weather",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "clear", "rain", "thunder" }, Hint = "<type>" } },
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "clear", "rain", "thunder" }, Hint = "<type>" }, intArg }
                }
            },
            new()
            {
                Name = "time",
                Description = "Change or query the time",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "set" }, Hint = "set" }, new() { Type = ArgType.Time, Hint = "<value>" } },
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "add" }, Hint = "add" }, intArg },
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "query" }, Hint = "query" }, new() { Type = ArgType.Literal, Choices = new[] { "daytime", "gametime", "day" }, Hint = "<query>" } }
                }
            },
            new()
            {
                Name = "worldborder",
                Description = "Manage the world border",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "set", "add", "get", "center", "damage", "warning" }, Hint = "<action>" }, free }
                }
            },

            // ── Player management ──
            new()
            {
                Name = "kick",
                Description = "Kick a player",
                Overloads = new()
                {
                    new() { sel },
                    new() { sel, msg }
                }
            },
            new()
            {
                Name = "ban",
                Description = "Ban a player",
                Overloads = new() { new() { sel, msg } }
            },
            new()
            {
                Name = "ban-ip",
                Description = "Ban an IP address",
                Overloads = new() { new() { free } }
            },
            new()
            {
                Name = "pardon",
                Description = "Pardon a banned player",
                Overloads = new() { new() { sel } }
            },
            new()
            {
                Name = "op",
                Description = "Grant operator status",
                Overloads = new() { new() { sel } }
            },
            new()
            {
                Name = "deop",
                Description = "Revoke operator status",
                Overloads = new() { new() { sel } }
            },
            new()
            {
                Name = "whitelist",
                Description = "Manage the whitelist",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "add", "remove", "list", "on", "off", "reload" }, Hint = "<action>" }, sel }
                }
            },
            new()
            {
                Name = "xp",
                Description = "Add or query experience",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "add", "set", "query" }, Hint = "<action>" }, sel, intArg },
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "add", "set" }, Hint = "<action>" }, sel, intArg, new() { Type = ArgType.Literal, Choices = new[] { "points", "levels" }, Hint = "points|levels" } }
                }
            },
            new()
            {
                Name = "experience",
                Description = "Add or query experience",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "add", "set", "query" }, Hint = "<action>" }, sel, intArg },
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "add", "set" }, Hint = "<action>" }, sel, intArg, new() { Type = ArgType.Literal, Choices = new[] { "points", "levels" }, Hint = "points|levels" } }
                }
            },
            new()
            {
                Name = "spawnpoint",
                Description = "Set a player's spawn point",
                Overloads = new()
                {
                    new() { sel },
                    new() { sel, pos }
                }
            },
            new()
            {
                Name = "setworldspawn",
                Description = "Set the world spawn point",
                Overloads = new()
                {
                    new() { pos },
                    new() { }
                }
            },
            new()
            {
                Name = "spectate",
                Description = "Make a player spectate an entity",
                Overloads = new() { new() { sel, sel } }
            },

            // ── Attributes ──
            new()
            {
                Name = "attribute",
                Description = "Query or modify entity attributes",
                Overloads = new()
                {
                    new()
                    {
                        sel,
                        new() { Type = ArgType.Attribute, Hint = "<attribute>" },
                        new() { Type = ArgType.Literal, Choices = new[] { "get", "base" }, Hint = "get|base" }
                    },
                    new()
                    {
                        sel,
                        new() { Type = ArgType.Attribute, Hint = "<attribute>" },
                        new() { Type = ArgType.Literal, Choices = new[] { "base" }, Hint = "base" },
                        new() { Type = ArgType.Literal, Choices = new[] { "set", "get" }, Hint = "set|get" },
                        floatArg
                    }
                }
            },

            // ── Execution ──
            new()
            {
                Name = "execute",
                Description = "Execute commands with conditions",
                Overloads = new()
                {
                    new()
                    {
                        new()
                        {
                            Type = ArgType.Literal,
                            Choices = new[] { "as", "at", "positioned", "rotated", "facing", "in", "anchored", "align", "if", "unless", "store", "run", "summon", "on" },
                            Hint = "<subcommand>"
                        },
                        free
                    }
                }
            },

            // ── Data / NBT ──
            new()
            {
                Name = "data",
                Description = "Get, merge, modify, or remove NBT data",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "get", "merge", "modify", "remove" }, Hint = "<action>" }, new() { Type = ArgType.Literal, Choices = new[] { "entity", "block", "storage" }, Hint = "entity|block|storage" }, free }
                }
            },
            new()
            {
                Name = "tag",
                Description = "Manage entity tags",
                Overloads = new()
                {
                    new() { sel, new() { Type = ArgType.Literal, Choices = new[] { "add", "remove", "list" }, Hint = "<action>" }, free }
                }
            },

            // ── Scoreboard ──
            new()
            {
                Name = "scoreboard",
                Description = "Manage scoreboard objectives and scores",
                Overloads = new()
                {
                    new()
                    {
                        new() { Type = ArgType.Literal, Choices = new[] { "objectives", "players" }, Hint = "objectives|players" },
                        new() { Type = ArgType.Literal, Choices = new[] { "list", "add", "remove", "setdisplay", "modify", "set", "get", "reset", "enable", "operation" }, Hint = "<action>" },
                        free
                    }
                }
            },

            // ── Teams ──
            new()
            {
                Name = "team",
                Description = "Manage teams",
                Overloads = new()
                {
                    new()
                    {
                        new() { Type = ArgType.Literal, Choices = new[] { "add", "remove", "empty", "join", "leave", "list", "modify" }, Hint = "<action>" },
                        free
                    }
                }
            },

            // ── Bossbars ──
            new()
            {
                Name = "bossbar",
                Description = "Manage boss bars",
                Overloads = new()
                {
                    new()
                    {
                        new() { Type = ArgType.Literal, Choices = new[] { "add", "get", "list", "remove", "set" }, Hint = "<action>" },
                        free
                    }
                }
            },

            // ── Sound / music ──
            new()
            {
                Name = "playsound",
                Description = "Play a sound",
                Overloads = new()
                {
                    new()
                    {
                        new() { Type = ArgType.Sound, Hint = "<sound>" },
                        new() { Type = ArgType.Literal, Choices = new[] { "master", "music", "record", "weather", "block", "hostile", "neutral", "player", "ambient", "voice" }, Hint = "<source>" },
                        sel
                    }
                }
            },
            new()
            {
                Name = "stopsound",
                Description = "Stop sounds",
                Overloads = new()
                {
                    new() { sel },
                    new() { sel, new() { Type = ArgType.Literal, Choices = new[] { "master", "music", "record", "weather", "block", "hostile", "neutral", "player", "ambient", "voice" }, Hint = "<source>" } }
                }
            },

            // ── Particles ──
            new()
            {
                Name = "particle",
                Description = "Display particles",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Particle, Hint = "<particle>" } },
                    new() { new() { Type = ArgType.Particle, Hint = "<particle>" }, pos },
                    new() { new() { Type = ArgType.Particle, Hint = "<particle>" }, pos, new() { Type = ArgType.Coordinate, Hint = "<delta>" }, floatArg, intArg }
                }
            },

            // ── Structure / locate ──
            new()
            {
                Name = "locate",
                Description = "Locate structures or biomes",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "structure", "biome", "poi" }, Hint = "structure|biome|poi" }, free }
                }
            },
            new()
            {
                Name = "place",
                Description = "Place features or structures",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "feature", "jigsaw", "structure", "template" }, Hint = "<type>" }, free }
                }
            },

            // ── Loot ──
            new()
            {
                Name = "loot",
                Description = "Drop or give loot",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "give", "insert", "spawn", "replace" }, Hint = "<target>" }, free }
                }
            },

            // ── Server ──
            new()
            {
                Name = "seed",
                Description = "Display the world seed",
                Overloads = new() { new() { } }
            },
            new()
            {
                Name = "list",
                Description = "List players",
                Overloads = new() { new() { }, new() { new() { Type = ArgType.Literal, Choices = new[] { "uuids" }, Hint = "uuids" } } }
            },
            new()
            {
                Name = "stop",
                Description = "Stop the server",
                Overloads = new() { new() { } }
            },
            new()
            {
                Name = "save-all",
                Description = "Save all data",
                Overloads = new() { new() { }, new() { new() { Type = ArgType.Literal, Choices = new[] { "flush" }, Hint = "flush" } } }
            },
            new()
            {
                Name = "save-on",
                Description = "Enable auto-saving",
                Overloads = new() { new() { } }
            },
            new()
            {
                Name = "save-off",
                Description = "Disable auto-saving",
                Overloads = new() { new() { } }
            },
            new()
            {
                Name = "reload",
                Description = "Reload data packs",
                Overloads = new() { new() { } }
            },
            new()
            {
                Name = "debug",
                Description = "Start or stop debug profiling",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "start", "stop", "function" }, Hint = "<action>" } }
                }
            },
            new()
            {
                Name = "publish",
                Description = "Open world to LAN",
                Overloads = new() { new() { } }
            },
            new()
            {
                Name = "perf",
                Description = "Performance profiling",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "start", "stop" }, Hint = "<action>" } }
                }
            },

            // ── Forceload / tick ──
            new()
            {
                Name = "forceload",
                Description = "Force chunks to stay loaded",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "add", "remove", "query" }, Hint = "<action>" }, free }
                }
            },
            new()
            {
                Name = "tick",
                Description = "Control game tick rate",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "query", "rate", "step", "sprint", "unfreeze", "freeze" }, Hint = "<action>" }, free }
                }
            },

            // ── Return / function ──
            new()
            {
                Name = "function",
                Description = "Run a function",
                Overloads = new() { new() { free } }
            },
            new()
            {
                Name = "return",
                Description = "Return a value from a function",
                Overloads = new()
                {
                    new() { intArg },
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "fail", "run" }, Hint = "fail|run" }, free }
                }
            },
            new()
            {
                Name = "schedule",
                Description = "Schedule a function",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "function", "clear" }, Hint = "function|clear" }, free }
                }
            },

            // ── Recipe / advancement ──
            new()
            {
                Name = "recipe",
                Description = "Give or take recipes",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "give", "take" }, Hint = "give|take" }, sel, free }
                }
            },
            new()
            {
                Name = "advancement",
                Description = "Grant or revoke advancements",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "grant", "revoke" }, Hint = "grant|revoke" }, sel, new() { Type = ArgType.Literal, Choices = new[] { "everything", "only", "from", "through", "until" }, Hint = "<mode>" }, free }
                }
            },

            // ── Misc ──
            new()
            {
                Name = "trigger",
                Description = "Set a trigger objective value",
                Overloads = new()
                {
                    new() { free, new() { Type = ArgType.Literal, Choices = new[] { "add", "set" }, Hint = "add|set" }, intArg }
                }
            },
            new()
            {
                Name = "help",
                Description = "Show help",
                Overloads = new() { new() { }, new() { free } }
            },
            new()
            {
                Name = "random",
                Description = "Generate a random value",
                Overloads = new()
                {
                    new() { new() { Type = ArgType.Literal, Choices = new[] { "value", "roll", "reset" }, Hint = "<action>" }, free }
                }
            },
        };
    }

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a list of suggestions for the given partial command input.
    /// </summary>
    public static List<CommandSuggestion> GetSuggestions(string input, int maxResults = 30)
    {
        if (string.IsNullOrEmpty(input))
            return GetTopLevelSuggestions("", maxResults);

        var text = input.TrimStart('/');
        var parts = text.Split(' ');
        var hasTrailingSpace = text.EndsWith(' ');

        if (parts.Length == 1 && !hasTrailingSpace)
            return GetTopLevelSuggestions(parts[0], maxResults);

        var cmdName = parts[0].ToLowerInvariant();
        var cmd = Commands.FirstOrDefault(c => c.Name.Equals(cmdName, StringComparison.OrdinalIgnoreCase));
        if (cmd == null)
            return new List<CommandSuggestion>();

        // argTokens = everything after the command name
        var argTokens = parts.Skip(1).ToArray();
        // tokenIndex is which arg-token the cursor is on
        // If trailing space, cursor is on a NEW token after last; else cursor is on the last token
        int tokenIndex = hasTrailingSpace ? argTokens.Length : argTokens.Length - 1;
        var currentToken = hasTrailingSpace ? "" : argTokens[^1];

        return GetArgumentSuggestions(cmd, argTokens, tokenIndex, currentToken, maxResults);
    }

    /// <summary>
    /// Returns a syntax hint for the current command being typed.
    /// </summary>
    public static string? GetSyntaxHint(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var text = input.TrimStart('/');
        var parts = text.Split(' ');
        var cmdName = parts[0].ToLowerInvariant();
        var cmd = Commands.FirstOrDefault(c => c.Name.Equals(cmdName, StringComparison.OrdinalIgnoreCase));
        if (cmd == null) return null;

        var overload = cmd.Overloads.FirstOrDefault();
        if (overload == null) return $"/{cmd.Name}";

        var syntax = $"/{cmd.Name}";
        foreach (var arg in overload)
        {
            if (arg.Type == ArgType.Literal && arg.Choices?.Length > 0)
                syntax += " " + string.Join("|", arg.Choices);
            else
                syntax += " " + arg.Hint;
        }

        return $"{syntax}  — {cmd.Description}";
    }

    // ── Command Builder API ─────────────────────────────────────────────────

    /// <summary>Builder step definition shown in the visual command builder UI.</summary>
    public sealed class BuilderStep
    {
        public string Label { get; init; } = "";
        public string Hint { get; init; } = "";
        public string[] Options { get; init; } = Array.Empty<string>();
        public bool IsFreeText { get; init; }
        public bool IsOptional { get; init; }
    }

    /// <summary>Commands available in the visual builder, grouped by category.</summary>
    public static readonly List<(string Category, string[] Commands)> BuilderCategories = new()
    {
        ("Entity",      new[] { "summon", "kill", "tp", "effect", "damage", "ride" }),
        ("Items",       new[] { "give", "clear", "enchant" }),
        ("Blocks",      new[] { "setblock", "fill", "clone" }),
        ("World",       new[] { "time", "weather", "difficulty", "gamemode", "gamerule", "worldborder" }),
        ("Chat",        new[] { "say", "title", "tellraw", "msg" }),
        ("Players",     new[] { "kick", "ban", "op", "deop", "xp", "spawnpoint" }),
        ("Advanced",    new[] { "execute", "scoreboard", "data", "tag", "function" }),
        ("Server",      new[] { "save-all", "reload", "stop", "list", "seed" }),
    };

    /// <summary>
    /// Returns the sequential builder steps for a given command name.
    /// </summary>
    public static List<BuilderStep> GetBuilderSteps(string commandName)
    {
        var cmd = Commands.FirstOrDefault(c => c.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));
        if (cmd == null) return new();

        // Pick the longest non-freeform overload for the richest builder experience
        var overload = cmd.Overloads
            .OrderByDescending(o => o.Count(a => a.Type != ArgType.FreeForm && a.Type != ArgType.Message))
            .ThenByDescending(o => o.Count)
            .FirstOrDefault();
        if (overload == null) return new();

        var steps = new List<BuilderStep>();
        foreach (var arg in overload)
        {
            steps.Add(ArgToBuilderStep(arg));
        }
        return steps;
    }

    private static BuilderStep ArgToBuilderStep(CommandArg arg)
    {
        return arg.Type switch
        {
            ArgType.Literal => new BuilderStep { Label = arg.Hint, Hint = "Select an option", Options = arg.Choices ?? Array.Empty<string>() },
            ArgType.Selector => new BuilderStep { Label = "Target", Hint = "Who to target", Options = Selectors },
            ArgType.Entity => new BuilderStep { Label = "Entity", Hint = "Entity type", Options = Entities },
            ArgType.Item => new BuilderStep { Label = "Item", Hint = "Item type", Options = Items },
            ArgType.Block => new BuilderStep { Label = "Block", Hint = "Block type", Options = Blocks },
            ArgType.Effect => new BuilderStep { Label = "Effect", Hint = "Status effect", Options = Effects },
            ArgType.Enchantment => new BuilderStep { Label = "Enchantment", Hint = "Enchantment type", Options = Enchantments },
            ArgType.Gamemode => new BuilderStep { Label = "Game Mode", Hint = "Select mode", Options = Gamemodes },
            ArgType.Difficulty => new BuilderStep { Label = "Difficulty", Hint = "Select difficulty", Options = Difficulties },
            ArgType.Boolean => new BuilderStep { Label = "Value", Hint = "true or false", Options = Booleans },
            ArgType.Time => new BuilderStep { Label = "Time", Hint = "Time value", Options = TimePresets },
            ArgType.Gamerule => new BuilderStep { Label = "Game Rule", Hint = "Select rule", Options = Gamerules },
            ArgType.Particle => new BuilderStep { Label = "Particle", Hint = "Particle type", Options = Particles },
            ArgType.Attribute => new BuilderStep { Label = "Attribute", Hint = "Attribute type", Options = Attributes },
            ArgType.Slot => new BuilderStep { Label = "Slot", Hint = "Equipment slot", Options = Slots },
            ArgType.Coordinate => new BuilderStep { Label = "Position (x y z)", Hint = "e.g. ~ ~ ~ or 100 64 200", Options = new[] { "~ ~ ~", "^ ^ ^", "~ ~1 ~", "^ ^ ^2" }, IsFreeText = true },
            ArgType.Integer => new BuilderStep { Label = arg.Hint, Hint = "Enter a number", Options = Array.Empty<string>(), IsFreeText = true },
            ArgType.Float => new BuilderStep { Label = arg.Hint, Hint = "Enter a decimal value", Options = Array.Empty<string>(), IsFreeText = true },
            ArgType.Nbt => new BuilderStep { Label = "NBT Data", Hint = "e.g. {NoAI:1b}", Options = new[] { "{}", "{NoAI:1b}", "{Silent:1b}", "{Invulnerable:1b}", "{Glowing:1b}", "{CustomName:\"\\\"name\\\"\"}", "{CustomNameVisible:1b}", "{PersistenceRequired:1b}", "{IsBaby:1b}" }, IsFreeText = true, IsOptional = true },
            ArgType.Message => new BuilderStep { Label = "Message", Hint = "Type your message", Options = Array.Empty<string>(), IsFreeText = true },
            _ => new BuilderStep { Label = arg.Hint, Hint = "Enter value", Options = Array.Empty<string>(), IsFreeText = true, IsOptional = true }
        };
    }

    // ── Internal logic ──────────────────────────────────────────────────────

    private static List<CommandSuggestion> GetTopLevelSuggestions(string filter, int max)
    {
        var results = new List<CommandSuggestion>();
        var lower = filter.ToLowerInvariant();

        foreach (var cmd in Commands)
        {
            if (string.IsNullOrEmpty(lower) || cmd.Name.StartsWith(lower, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new CommandSuggestion
                {
                    InsertText = cmd.Name,
                    Label = "/" + cmd.Name,
                    Description = cmd.Description,
                    Category = SuggestionCategory.Command
                });
            }
        }

        return results.OrderBy(s => s.InsertText).Take(max).ToList();
    }

    /// <summary>
    /// Walks each overload token-by-token, respecting that Coordinate consumes 3 tokens,
    /// to find which semantic argument the cursor falls on.
    /// </summary>
    private static List<CommandSuggestion> GetArgumentSuggestions(
        CommandDef cmd, string[] argTokens, int cursorTokenIndex, string filter, int max)
    {
        var argTypes = new HashSet<ArgType>();
        var literalChoices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var overload in cmd.Overloads)
        {
            int tok = 0; // current token position being consumed
            foreach (var arg in overload)
            {
                int width = TokenWidth(arg.Type);

                // Is the cursor within the token range consumed by this arg?
                if (width == int.MaxValue)
                {
                    // Greedy arg (Message/FreeForm) — matches if cursor >= tok
                    if (cursorTokenIndex >= tok)
                    {
                        argTypes.Add(arg.Type);
                        if (arg.Type == ArgType.Literal && arg.Choices != null)
                            foreach (var c in arg.Choices) literalChoices.Add(c);
                    }
                    break; // greedy consumes everything
                }

                if (cursorTokenIndex >= tok && cursorTokenIndex < tok + width)
                {
                    // Cursor is inside this argument's token span
                    if (arg.Type == ArgType.Coordinate)
                    {
                        // Which sub-token of the coordinate? (0=x, 1=y, 2=z)
                        int subIndex = cursorTokenIndex - tok;
                        // Still suggest coordinate-style completions
                        argTypes.Add(ArgType.Coordinate);
                    }
                    else
                    {
                        argTypes.Add(arg.Type);
                        if (arg.Type == ArgType.Literal && arg.Choices != null)
                            foreach (var c in arg.Choices) literalChoices.Add(c);
                    }
                }

                tok += width;
                if (tok > cursorTokenIndex) break; // past cursor, no need to check further
            }
        }

        var results = new List<CommandSuggestion>();
        var lower = filter.ToLowerInvariant();

        // Add literal choices
        foreach (var choice in literalChoices)
        {
            if (Matches(choice, lower))
                results.Add(new CommandSuggestion { InsertText = choice, Label = choice, Description = "keyword", Category = SuggestionCategory.Keyword });
        }

        // Add type-specific suggestions
        foreach (var type in argTypes)
        {
            switch (type)
            {
                case ArgType.Selector:
                    AddFiltered(results, Selectors, lower, SuggestionCategory.Selector, "target selector");
                    break;
                case ArgType.Entity:
                    AddFiltered(results, Entities, lower, SuggestionCategory.Entity, "entity type");
                    AddFilteredShortForm(results, Entities, lower, SuggestionCategory.Entity, "entity type");
                    break;
                case ArgType.Item:
                    AddFiltered(results, Items, lower, SuggestionCategory.Item, "item");
                    AddFilteredShortForm(results, Items, lower, SuggestionCategory.Item, "item");
                    break;
                case ArgType.Block:
                    AddFiltered(results, Blocks, lower, SuggestionCategory.Block, "block");
                    AddFilteredShortForm(results, Blocks, lower, SuggestionCategory.Block, "block");
                    break;
                case ArgType.Effect:
                    AddFiltered(results, Effects, lower, SuggestionCategory.Effect, "effect");
                    AddFilteredShortForm(results, Effects, lower, SuggestionCategory.Effect, "effect");
                    break;
                case ArgType.Enchantment:
                    AddFiltered(results, Enchantments, lower, SuggestionCategory.Enchantment, "enchantment");
                    AddFilteredShortForm(results, Enchantments, lower, SuggestionCategory.Enchantment, "enchantment");
                    break;
                case ArgType.Gamemode:
                    AddFiltered(results, Gamemodes, lower, SuggestionCategory.Keyword, "game mode");
                    break;
                case ArgType.Difficulty:
                    AddFiltered(results, Difficulties, lower, SuggestionCategory.Keyword, "difficulty");
                    break;
                case ArgType.Boolean:
                    AddFiltered(results, Booleans, lower, SuggestionCategory.Keyword, "boolean");
                    break;
                case ArgType.Time:
                    AddFiltered(results, TimePresets, lower, SuggestionCategory.Keyword, "time value");
                    break;
                case ArgType.Gamerule:
                    AddFiltered(results, Gamerules, lower, SuggestionCategory.Keyword, "game rule");
                    break;
                case ArgType.Particle:
                    AddFiltered(results, Particles, lower, SuggestionCategory.Particle, "particle");
                    AddFilteredShortForm(results, Particles, lower, SuggestionCategory.Particle, "particle");
                    break;
                case ArgType.Attribute:
                    AddFiltered(results, Attributes, lower, SuggestionCategory.Keyword, "attribute");
                    AddFilteredShortForm(results, Attributes, lower, SuggestionCategory.Keyword, "attribute");
                    break;
                case ArgType.Slot:
                    AddFiltered(results, Slots, lower, SuggestionCategory.Keyword, "slot");
                    break;
                case ArgType.BiomeId:
                    AddFiltered(results, Biomes, lower, SuggestionCategory.Keyword, "biome");
                    AddFilteredShortForm(results, Biomes, lower, SuggestionCategory.Keyword, "biome");
                    break;
                case ArgType.Dimension:
                    AddFiltered(results, Dimensions, lower, SuggestionCategory.Keyword, "dimension");
                    AddFilteredShortForm(results, Dimensions, lower, SuggestionCategory.Keyword, "dimension");
                    break;
                case ArgType.Structure:
                    AddFiltered(results, Structures, lower, SuggestionCategory.Keyword, "structure");
                    AddFilteredShortForm(results, Structures, lower, SuggestionCategory.Keyword, "structure");
                    break;
                case ArgType.Color:
                    AddFiltered(results, Colors, lower, SuggestionCategory.Keyword, "color");
                    break;
                case ArgType.Coordinate:
                    if (string.IsNullOrEmpty(filter))
                    {
                        results.Add(new CommandSuggestion { InsertText = "~", Label = "~", Description = "relative", Category = SuggestionCategory.Coordinate });
                        results.Add(new CommandSuggestion { InsertText = "^", Label = "^", Description = "local", Category = SuggestionCategory.Coordinate });
                        results.Add(new CommandSuggestion { InsertText = "~1", Label = "~1", Description = "relative +1", Category = SuggestionCategory.Coordinate });
                        results.Add(new CommandSuggestion { InsertText = "~-1", Label = "~-1", Description = "relative -1", Category = SuggestionCategory.Coordinate });
                        results.Add(new CommandSuggestion { InsertText = "^2", Label = "^2", Description = "local +2", Category = SuggestionCategory.Coordinate });
                        results.Add(new CommandSuggestion { InsertText = "0", Label = "0", Description = "absolute 0", Category = SuggestionCategory.Coordinate });
                    }
                    break;
                case ArgType.Nbt:
                    if (string.IsNullOrEmpty(filter))
                    {
                        results.Add(new CommandSuggestion { InsertText = "{}", Label = "{}", Description = "empty NBT compound", Category = SuggestionCategory.Nbt });
                        results.Add(new CommandSuggestion { InsertText = "{NoAI:1b}", Label = "{NoAI:1b}", Description = "no AI", Category = SuggestionCategory.Nbt });
                        results.Add(new CommandSuggestion { InsertText = "{Silent:1b}", Label = "{Silent:1b}", Description = "silent", Category = SuggestionCategory.Nbt });
                        results.Add(new CommandSuggestion { InsertText = "{Invulnerable:1b}", Label = "{Invulnerable:1b}", Description = "invulnerable", Category = SuggestionCategory.Nbt });
                        results.Add(new CommandSuggestion { InsertText = "{CustomName:\"\\\"name\\\"\"}", Label = "{CustomName:\"name\"}", Description = "custom name", Category = SuggestionCategory.Nbt });
                        results.Add(new CommandSuggestion { InsertText = "{CustomNameVisible:1b}", Label = "{CustomNameVisible:1b}", Description = "show name", Category = SuggestionCategory.Nbt });
                        results.Add(new CommandSuggestion { InsertText = "{Glowing:1b}", Label = "{Glowing:1b}", Description = "glowing", Category = SuggestionCategory.Nbt });
                        results.Add(new CommandSuggestion { InsertText = "{PersistenceRequired:1b}", Label = "{PersistenceRequired:1b}", Description = "won't despawn", Category = SuggestionCategory.Nbt });
                        results.Add(new CommandSuggestion { InsertText = "{IsBaby:1b}", Label = "{IsBaby:1b}", Description = "baby mob", Category = SuggestionCategory.Nbt });
                        results.Add(new CommandSuggestion { InsertText = "{Size:4}", Label = "{Size:4}", Description = "slime/magma cube size", Category = SuggestionCategory.Nbt });
                    }
                    break;
            }
        }

        // Selector parameter context (user typed @e[ or @a[...)
        if (filter.Contains('[') && !filter.Contains(']'))
        {
            results.Clear();
            var afterBracket = filter.Substring(filter.LastIndexOf('[') + 1);
            var lastComma = afterBracket.LastIndexOf(',');
            var paramToken = lastComma >= 0 ? afterBracket[(lastComma + 1)..] : afterBracket;
            var paramLower = paramToken.ToLowerInvariant().Trim();

            if (!paramToken.Contains('='))
            {
                foreach (var sp in SelectorParameters)
                {
                    if (Matches(sp, paramLower))
                        results.Add(new CommandSuggestion { InsertText = sp, Label = sp, Description = "selector parameter", Category = SuggestionCategory.Selector });
                }
            }
            else if (paramToken.Contains("type="))
            {
                var valueToken = paramToken.Split('=').Last().ToLowerInvariant();
                AddFiltered(results, Entities, valueToken, SuggestionCategory.Entity, "entity type");
                AddFilteredShortForm(results, Entities, valueToken, SuggestionCategory.Entity, "entity type");
            }
            else if (paramToken.Contains("gamemode="))
            {
                var valueToken = paramToken.Split('=').Last().ToLowerInvariant();
                AddFiltered(results, Gamemodes, valueToken, SuggestionCategory.Keyword, "game mode");
            }
            else if (paramToken.Contains("sort="))
            {
                var valueToken = paramToken.Split('=').Last().ToLowerInvariant();
                AddFiltered(results, new[] { "nearest", "furthest", "random", "arbitrary" }, valueToken, SuggestionCategory.Keyword, "sort mode");
            }
        }

        return results
            .GroupBy(s => s.InsertText, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(s => s.InsertText.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s.InsertText)
            .Take(max)
            .ToList();
    }

    private static bool Matches(string value, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddFiltered(List<CommandSuggestion> results, string[] values, string filter,
        SuggestionCategory category, string description)
    {
        foreach (var v in values)
        {
            if (Matches(v, filter))
                results.Add(new CommandSuggestion { InsertText = v, Label = v, Description = description, Category = category });
        }
    }

    private static void AddFilteredShortForm(List<CommandSuggestion> results, string[] values, string filter,
        SuggestionCategory category, string description)
    {
        foreach (var v in values)
        {
            if (v.StartsWith("minecraft:"))
            {
                var shortName = v["minecraft:".Length..];
                if (Matches(shortName, filter) && !filter.Contains(':'))
                    results.Add(new CommandSuggestion { InsertText = v, Label = shortName, Description = description, Category = category });
            }
        }
    }
}

public enum SuggestionCategory
{
    Command,
    Keyword,
    Selector,
    Entity,
    Item,
    Block,
    Effect,
    Enchantment,
    Particle,
    Coordinate,
    Nbt
}

public class CommandSuggestion
{
    public string InsertText { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public SuggestionCategory Category { get; set; }
}
