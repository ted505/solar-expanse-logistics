# UI Guide

The Logistics tab appears in the Object Info window for any body owned by the player. It contains four collapsible sections.

## GET (Import Resources)

Configure what resources this body needs and how much.

### Creating a rule

1. Click **+ Add Import Rule**.
2. The resource picker shows three categories:
   - **Available**: resources with surplus from SEND providers elsewhere in the network.
   - **Market only** (`[MARKET]` label): resources not in the logistics network but with active sell offers on this body. Can be fulfilled via auto-buy.
   - **Not available** (greyed): resources with no known source.
3. Select a resource, then set:
   - **Target amount**: the desired stockpile level.
   - **Minimum amount** (optional toggle): the reorder threshold. Logistics waits until stock drops below minimum before dispatching, then fills to target.
   - **One-shot**: removes the rule after one successful delivery.
   - **Priority**: Low / Normal / High / Critical. Higher priority requests are fulfilled first.
   - **Auto-buy**: automatically purchases from local market sell offers up to a max price. Default price seeds from the cheapest matching offer.

### Summary row

Each active rule shows:
- **Resource name** (bold white)
- Amount info: `target 5KT, min 2KT` or just `5KT`
- Mode flags: `one-shot`, `auto-buy <= $X`
- Status: `[pending]`, `[in transit]`, `[satisfied]`, `[failed]`
- Status notes: blocker reasons, transit vehicle info, arrival estimates
- **Priority badge** (right-aligned, color-coded): Low (blue-grey), Normal (grey), High (amber), Critical (red)
- **EDIT** button: re-opens the amount input with current values
- **X** button: removes the rule

## SEND (Export Resources)

Configure what resources this body exports and how much to keep in reserve.

### Creating a rule

1. Click **+ Add Export Rule**.
2. Select a resource (only locally available resources shown).
3. Set:
   - **Reserve amount**: minimum stock to keep locally. Surplus above this is available for logistics.
   - **Priority**: Low / Normal / High / Critical.
   - **Auto-sell**: automatically sells to local market buy offers.
     - **Continuous**: sells whenever a matching offer exists above minimum price.
     - **Per month**: caps total sold per month. Resets each in-game month.
     - **Minimum price**: floor price for auto-sell transactions.

### Summary row

Same layout as GET: bold resource name, reserve amount, auto-sell details, priority badge, EDIT/X buttons.

## SPACECRAFT (Logistics Vessels)

Assign spacecraft quotas to determine which ships are available for logistics at this body.

### Quota rows

Each row shows:
- Ship type name
- Available/assigned count: `2/7` means 2 ready here out of 7 quota
- **Transfer preference**: toggle between Fastest and Optimal trajectory planning
- **Minimum shipment**: minimum useful cargo load. Ships won't dispatch with less than this amount.
- **+/-** buttons: adjust quota count
- **X** button: remove quota entirely

### Adding quotas

Click **+ Add** to see a picker of spacecraft types present at this body (including ships in low orbit, shown with `[ORBIT]` suffix). Selecting a type creates a quota of 1.

## LAUNCH VEHICLE (Surface Shuttles)

Toggle launch vehicle types available for logistics surface-to-orbit lifts.

### LV rows

Each row is a toggle button. Enabled LVs (green-tinted) are available for logistics staging. Disabled LVs (dark) are not used. Includes facility-backed launch support (Space Elevators, Magnetic Launch Rails, etc.).

## Number Formatting

All amounts use compact notation:
- `1.2MT` = 1,200,000 tons
- `5KT` = 5,000 tons  
- `800T` = 800 tons
- `9.5T` = 9.5 tons

## Stock Integration

- **Spacecraft rows**: Stock spacecraft list rows show `[LOGI X pool]` (blue) for ships available through a shared logistics quota, `[LOGI X assigned]` (blue) for ships assigned to a specific SEND provider, and `[LOGI X return]` (orange) for ships expected to return from logistics trips.
- **Mission names**: Logistics missions appear in the stock mission list as `[LOGI] ResourceIcon` (outbound) and `[LOGI-RETURN]` (return trip).
- **Notifications**: Cyclical arrival notifications are suppressed for logistics missions to reduce spam.
