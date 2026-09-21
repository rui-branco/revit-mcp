// Site, hardscape and family placement: the tools that put modelled elements in
// the document rather than drawing-side ones.
//
// Like every other write, the Revit side wraps a whole call in one transaction
// group, so a batch of 200 points is exactly one Ctrl+Z for the user.
//
// All lengths and coordinates are Revit internal units: decimal feet.

import { z } from "zod";

const point3 = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number().optional().describe("Elevation in feet; defaults to 0"),
});

// z is the surface elevation at that point for a toposolid, so it is not optional there.
const surveyPoint = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number().describe("Surface elevation at this point, in feet"),
});

const point2 = z.object({ x: z.number(), y: z.number() });

export function registerModelTools(server, bridge) {
  server.tool(
    "revit_create_toposolid",
    "Create the site surface from a cloud of survey points. Uses a Revit 2024+ Toposolid when the document has a toposolid type, and falls back to the legacy TopographySurface when it has none — the response says which in 'type'. Points are x/y/z in feet (Revit internal units) and z is the surface elevation at that point, so it matters here. One call is one undo step.",
    {
      points: z
        .array(surveyPoint)
        .min(3)
        .describe("Survey points defining the top face, at least 3, in feet"),
      type_name: z
        .string()
        .min(1)
        .optional()
        .describe("Toposolid type name. Omit it to use the first one in the document."),
      level: z
        .string()
        .min(1)
        .optional()
        .describe("Level the toposolid is hosted on. Omit it to use the lowest level in the document."),
    },
    async ({ points, type_name, level }) => {
      try {
        const result = await bridge.call("/toposolid/create", {
          points,
          typeName: type_name,
          level,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_flatten_toposolid",
    "Flatten a region of the site surface to one elevation, so paving can sit on graded terrain instead of fighting it — this is the fix for Revit's 'Highlighted toposolid and floor overlap' warning. 'points' is a ring in plan and 'elevation' is what to level it to, both in feet (Revit internal units). The ring is added to the surface at that elevation, creased so the flat region ends at its boundary, and every existing surface point inside it is moved to match. The response reports 'residual' — the largest distance any point in the region is still off the target — so check that rather than assuming it worked. Only a Revit 2024+ Toposolid can be flattened; a legacy TopographySurface cannot. One call is one undo step.",
    {
      points: z
        .array(point2)
        .min(3)
        .describe("Region boundary in plan, in feet. Closed automatically; at least 3 points."),
      elevation: z.number().describe("Elevation to flatten the region to, in feet"),
      toposolid_id: z
        .number()
        .int()
        .optional()
        .describe("Id of the toposolid to flatten. Only needed when the document has more than one."),
    },
    async ({ points, elevation, toposolid_id }) => {
      try {
        const result = await bridge.call("/toposolid/flatten", {
          points,
          elevation,
          toposolidId: toposolid_id,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_floor",
    "Create a floor from a closed boundary: paving, a pool deck, a terrace, or an actual building floor. The boundary is a ring of x/y points in feet (Revit internal units) taken at the level's elevation, and it is closed automatically when the last point is not the first. 'offset' lifts the floor off its level (Revit's 'Height Offset From Level'), which is how paving clears a graded toposolid instead of interpenetrating it — the other half of that fix is revit_flatten_toposolid. One call is one undo step.",
    {
      level: z.string().min(1).describe("Level name the floor is hosted on"),
      boundary: z
        .array(point2)
        .min(3)
        .describe("Boundary ring in plan, in feet. Closed automatically; at least 3 points."),
      type_name: z
        .string()
        .min(1)
        .optional()
        .describe("Floor type name, e.g. 'Generic - 300mm'. Omit it to use the document default."),
      structural: z
        .boolean()
        .optional()
        .describe("True for a structural floor, false (the default) for architectural"),
      offset: z
        .number()
        .optional()
        .describe(
          "Height offset from the level in feet, positive up. Defaults to 0. Written to the floor's 'Height Offset From Level' parameter and read back into the response.",
        ),
    },
    async ({ level, boundary, type_name, structural, offset }) => {
      try {
        const result = await bridge.call("/floors/create", {
          level,
          boundary,
          typeName: type_name,
          structural,
          offset,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_load_families",
    "Load .rfa family files into the project — real trees, benches, bollards, light fittings, doors and windows instead of DirectShape primitives. Autodesk's library is an optional download that lives OUTSIDE the project, under C:\\ProgramData\\Autodesk\\RVT <year>\\Libraries\\<language>\\ (Planting\\, Site\\Accessories\\, Lighting\\Architectural\\External\\, Furniture\\, Doors\\, Windows\\), so nothing in it exists to revit_list_family_symbols or revit_place_families until it is loaded here first. A library one Revit release behind loads fine — Revit upgrades the family on the way in, and anything it warned about comes back in 'warnings'. Every file comes back with the family name and its type ids, so you can place immediately without a second lookup. A family already in the project is reloaded rather than refused, keeping the parameter values the project has set. Read 'loaded' and 'alreadyLoaded' together: loaded false with alreadyLoaded true is not a failure, it means Revit found the project's copy identical to the file and did nothing — the familyName and symbols on that row are still the ones you want. Nothing aborts the batch: a missing file is FILE_NOT_FOUND on its own row and the rest still load. One call is one undo step.",
    {
      paths: z
        .array(z.string().min(1))
        .min(1)
        .optional()
        .describe("Full paths of .rfa files to load. Pass the whole batch in one call."),
      path: z.string().min(1).optional().describe("A single .rfa path, as a shorthand for paths"),
    },
    async ({ paths, path }) => {
      try {
        if (!paths && !path) {
          return {
            content: [
              { type: "text", text: "Error: pass 'paths' (an array of .rfa files) or 'path' (one)." },
            ],
          };
        }
        const result = await bridge.call("/families/load", paths ? { paths } : { path });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_list_family_symbols",
    "List the family types loaded in the document: id, family name, type name and category. This is how you discover what revit_place_families can actually place. Filter by 'category', by 'family_name', or both — 'family_name' matches the family, so right after revit_load_families you can ask for exactly the file you just loaded. An empty list means no family content is loaded: load some with revit_load_families, and only fall back to revit_create_directshape or revit_place_planting if the library is genuinely not installed.",
    {
      category: z
        .string()
        .min(1)
        .optional()
        .describe("Category name to filter by, e.g. 'Planting' or 'OST_LightingFixtures'"),
      family_name: z
        .string()
        .min(1)
        .optional()
        .describe(
          "Family name to filter by, e.g. 'M_RPC Tree - Deciduous'. This is the family, not the type — it returns every type in that family.",
        ),
    },
    async ({ category, family_name }) => {
      try {
        const result = await bridge.call("/families/symbols", {
          category,
          familyName: family_name,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_place_families",
    "Place one family instance per point — trees, shrubs, light fittings, furniture. Get symbol_id from revit_list_family_symbols (and put the family there first with revit_load_families). The symbol is activated for you if it has never been used. Points are x/y/z in feet (Revit internal units) and z is an ABSOLUTE model elevation, the same as everywhere else in this MCP — the bridge converts it to the level offset Revit's placement API actually wants, so a tree asked for at z=0 stands at z=0 whatever level it is on. 'level' is optional and defaults to the lowest level, which is where site content belongs; 'z' is an extra offset added to every point, for lifting a whole batch onto a terrace. Each placed point reports 'placedZ' read back off the instance, so you can check where things actually landed rather than trusting it. rotation is radians about the vertical axis. Pass every point in one call: the whole batch is one undo step, and a point Revit refuses comes back under 'failed' while the rest are still placed.",
    {
      symbol_id: z
        .number()
        .int()
        .describe("Family type id from revit_list_family_symbols"),
      level: z
        .string()
        .min(1)
        .optional()
        .describe("Level name the instances are associated with. Omit it for the lowest level."),
      points: z
        .array(point3)
        .min(1)
        .describe(
          "Insertion points in feet, one instance per point, z an absolute model elevation. All in this one call.",
        ),
      z: z
        .number()
        .optional()
        .describe("Extra elevation in feet added to every point's own z. Defaults to 0."),
      rotation: z
        .number()
        .optional()
        .describe(
          "Rotation in radians about the vertical axis through each point, counter-clockwise in plan. Radians is Revit's internal angle unit, as feet is its internal length unit.",
        ),
    },
    async ({ symbol_id, level, points, z: zOffset, rotation }) => {
      try {
        const result = await bridge.call("/families/place", {
          symbolId: symbol_id,
          level,
          points,
          z: zOffset,
          rotation,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_place_openings",
    "Place doors and windows IN walls — the tool for family instances that have to cut their host, which is why it is not revit_place_families with a flag. A door or window placed unhosted stands in front of an uncut wall: nearly right in plan, wrong in every 3D view, and the most common way a model of a house stays a sealed box. Get symbol_id from revit_list_family_symbols after revit_load_families; Autodesk's library keeps them under Doors\\ and Windows\\ (and Doors\\Residential\\ for the exterior ones). The symbol is activated for you if it has never been used. host_wall_id is OPTIONAL and leaving it out is the normal case: for each point the bridge projects onto every wall's location line and hosts in the nearest within 3 feet, then reports the wall each instance actually landed in — read back off the instance's host, not echoed, so a symbol that is not wall-hosted shows up as a null hostWallId rather than being assumed to have worked. A point with no wall near it comes back under 'failed' with NO_HOST_WALL and the rest of the batch still lands. Points are x/y/z in feet (Revit internal units) along the wall; z is the absolute model elevation of the insertion point. sill_height is feet above the level: omit it and a Windows-category symbol gets 3 feet (a window on the floor is not a window) while anything else keeps Revit's own. The row reports sillHeight read back and sillHeightOn — 'instance' normally, 'type' when the family keeps its sill on the type, and writing the type's moves every other instance of that type. Pass every opening in one call: the whole batch is one undo step.",
    {
      symbol_id: z
        .number()
        .int()
        .describe("Door or window family type id from revit_list_family_symbols"),
      points: z
        .array(point3)
        .min(1)
        .describe(
          "Insertion points in feet, one opening per point, z an absolute model elevation. All in this one call.",
        ),
      host_wall_id: z
        .number()
        .int()
        .optional()
        .describe(
          "Wall to host every point in. Omit it to let the bridge host each point in the nearest wall within 3 feet and report which one it picked.",
        ),
      level: z
        .string()
        .min(1)
        .optional()
        .describe("Level name the openings are associated with. Omit it for the lowest level."),
      sill_height: z
        .number()
        .optional()
        .describe(
          "Height of the sill above the level, in feet. Omit it for 3 feet on a window and Revit's own on anything else.",
        ),
    },
    async ({ symbol_id, points, host_wall_id, level, sill_height }) => {
      try {
        // The tool arguments stay snake_case like every other tool's; the bridge
        // reads symbolId / hostWallId / sillHeight — map them here rather than on
        // the C# side.
        const result = await bridge.call("/openings/place", {
          symbolId: symbol_id,
          points,
          hostWallId: host_wall_id,
          level,
          sillHeight: sill_height,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
