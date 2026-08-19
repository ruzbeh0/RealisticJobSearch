# Realistic JobSearch

Realistic JobSearch replaces vanilla job assignment with a gravity model so commuting behavior is more believable.

## What it changes

- **Distance attractiveness**: Nearby workplaces are preferred.
- **Employer size attractiveness**: Large workplaces pull workers from farther away.
- Workers end up clustering around major employers while still allowing cross-city commutes.

## How the model works

The selection combines two factors:

1. **Distance from home/worker to workplace** (closer is better).
2. **Employer attractiveness by size** (larger workplaces have more pull).

Both are configurable to tune how strict or relaxed the selection is.

## Features

- Gravity-based job matching instead of vanilla random assignment.
- Realistic commuting clusters while preserving long-distance commuting behavior.
- Optional CSV output for analysis.

## Compatibility note

- Recommended to use alongside **Realistic Workplaces and Households** for best results.
- Vanilla behavior can under-assign employees to high-density offices, which can make commutes feel less realistic when this mod is used alone.

## Mod metadata

- **Mod ID:** 123500
- **Current version:** 0.2.2
- **Game version target:** 1.6.*
- **Forum:** [Realistic JobSearch thread](https://forum.paradoxplaza.com/forum/threads/realistic-jobsearch-mod.1865546/)
- **GitHub:** [github.com/ruzbeh0/RealisticJobSearch](https://github.com/ruzbeh0/RealisticJobSearch)
- **Discord:** [Channel link](https://discord.com/channels/1024242828114673724/1433644564136333484)
