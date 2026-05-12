using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

public sealed class PromptTemplatesService
{
    private readonly List<PromptTemplate> _templates = new()
    {
        new PromptTemplate
        {
            Id = "solar-system",
            Name = "Solar System",
            Category = "3D Animation",
            Icon = "🪐",
            Prompt = "Create a fully self-contained single HTML file with an animated 3D solar system using CSS 3D transforms and JavaScript. Render the Sun at the center with at least 6 planets orbiting at different speeds and distances. Each planet should have a distinct color, size, and axial tilt. Add a starfield background, planet labels on hover, and orbital path rings. Everything must run instantly in the browser with no external dependencies — pure HTML, CSS, and vanilla JS only."
        },
        new PromptTemplate
        {
            Id = "physics-balls",
            Name = "Physics Balls",
            Category = "Physics Demo",
            Icon = "🎱",
            Prompt = "Build a self-contained single HTML file physics sandbox using an HTML5 Canvas. Simulate 30+ bouncing balls with realistic gravity, friction, and elastic collisions between balls and walls. Each ball should have a random radius, mass, color, and initial velocity. Add a trail effect behind fast-moving balls. Let the user click to spawn new balls and right-click to apply an explosion force. No external libraries — implement the physics engine from scratch in vanilla JavaScript."
        },
        new PromptTemplate
        {
            Id = "cloth-sim",
            Name = "Cloth Simulation",
            Category = "Physics Demo",
            Icon = "🧵",
            Prompt = "Create a self-contained single HTML file cloth simulation on an HTML5 Canvas. Model the cloth as a grid of particles connected by spring constraints (Verlet integration). Render it with thin lines between connected nodes. The top row of nodes should be pinned. Allow the user to click and drag to tear the cloth, and press R to reset. Add wind that sways the cloth. Implement gravity and damping. No external libraries — pure vanilla JavaScript physics."
        },
        new PromptTemplate
        {
            Id = "particle-galaxy",
            Name = "Particle Galaxy",
            Category = "3D Animation",
            Icon = "🌌",
            Prompt = "Produce a self-contained single HTML file that renders an interactive 3D particle galaxy using an HTML5 Canvas and vanilla JavaScript. Generate 8,000+ particles arranged in spiral galaxy arms with a dense galactic core. Animate slow rotation. Let the user click-drag to orbit the camera around the galaxy and scroll to zoom. Color particles by distance from center — hot white/blue core fading to cool purple/red edges. No WebGL libraries; project 3D points manually to 2D with perspective division."
        },
        new PromptTemplate
        {
            Id = "tower-of-hanoi",
            Name = "Tower of Hanoi",
            Category = "3D Animation",
            Icon = "🗼",
            Prompt = "Create a self-contained single HTML file animated Tower of Hanoi solver using CSS 3D transforms. Render three pegs and 6 discs as 3D rounded rectangles with distinct gradient colors. When the user clicks Solve, animate the optimal solution step-by-step with smooth arc movement for each disc. Show the move counter and recursion depth. Allow the user to change the number of discs (3–8). Dark background, soft lighting shadows on the discs. Pure HTML, CSS, and vanilla JS."
        },
        new PromptTemplate
        {
            Id = "soft-body",
            Name = "Soft Body Blob",
            Category = "Physics Demo",
            Icon = "🫧",
            Prompt = "Build a self-contained single HTML file that simulates a squishy soft-body blob on an HTML5 Canvas. Model the blob as a ring of particles connected by spring constraints to a central anchor point and to their neighbours. Apply gravity so it falls and squishes on impact with the floor. Let the user drag the blob around. When released, it should jiggle and settle. Render it as a smooth filled shape using bezier curves through the particle positions. Vanilla JS only — no physics libraries."
        },
        new PromptTemplate
        {
            Id = "raymarcher",
            Name = "Ray Marcher",
            Category = "3D Animation",
            Icon = "💎",
            Prompt = "Create a self-contained single HTML file real-time ray marcher rendered on an HTML5 Canvas using vanilla JavaScript. Render a scene with at least three signed-distance-function (SDF) primitives: a sphere, a torus, and a box with smooth blending between them. Animate the shapes rotating and pulsing. Implement Phong shading with a directional light, ambient occlusion approximation, and a reflective floor plane. Target 30+ FPS by rendering at half resolution and upscaling. No WebGL — pure CPU ray marching in a 2D canvas."
        },
        new PromptTemplate
        {
            Id = "collider-playground",
            Name = "Collider Playground",
            Category = "Physics Demo",
            Icon = "🟦",
            Prompt = "Build a self-contained single HTML file 2D rigid-body collider playground on an HTML5 Canvas. Support circles, rectangles, and triangles with accurate SAT (Separating Axis Theorem) collision detection and impulse-based resolution. Spawn random shapes from a toolbar at the top. Add static platform shapes the user can draw by clicking and dragging. Simulate gravity, friction, and restitution. Show contact normals as small arrows. Vanilla JS physics engine from scratch — no Box2D or other libraries."
        },
        new PromptTemplate
        {
            Id = "ragdoll",
            Name = "Ragdoll Physics",
            Category = "Physics Demo",
            Icon = "🪆",
            Prompt = "Create a self-contained single HTML file 2D ragdoll physics simulation on an HTML5 Canvas. Model the ragdoll as a skeleton of rigid segments (head, torso, upper/lower arms, upper/lower legs) connected by hinge joints with angular limits. Implement constraint solving via iterative position projection (XPBD or Verlet). Drop the ragdoll from the top when the page loads; it should crumple realistically on a platform floor. Let the user grab and fling any limb with the mouse. Add at least one more ragdoll spawned by pressing Space. Vanilla JS only — no physics libraries."
        },
        new PromptTemplate
        {
            Id = "ragdoll-stack",
            Name = "Ragdoll Pile",
            Category = "Physics Demo",
            Icon = "🧸",
            Prompt = "Build a self-contained single HTML file that spawns ragdolls continuously from the top of the screen. Each ragdoll is a chain of 8 rigid limb segments with Verlet-integrated joints. They fall and pile on top of each other and on randomly placed static ledges. Simulate up to 10 ragdolls simultaneously with joint constraint solving and body-body collision response. Render limbs as rounded capsules with a cartoon character face on the head. Press R to clear and restart. Pure HTML Canvas and vanilla JS — no external physics engine."
        },
        new PromptTemplate
        {
            Id = "3d-cube-engine",
            Name = "3D Object Viewer",
            Category = "3D Animation",
            Icon = "🧊",
            Prompt = "Create a self-contained single HTML file software 3D renderer on an HTML5 Canvas using vanilla JavaScript. Load and render at least three hard-coded 3D mesh objects: a cube, an icosphere, and a torus. Implement a full vertex transformation pipeline: model → world → view → perspective projection. Render filled polygons with a flat-shading Phong lighting model (ambient + diffuse + specular) using a painter's algorithm for depth sorting. Allow the user to click-drag to rotate the selected object and scroll to zoom the camera. No WebGL — pure CPU rasterisation."
        },
        new PromptTemplate
        {
            Id = "metaballs-3d",
            Name = "Metaballs",
            Category = "3D Animation",
            Icon = "🫀",
            Prompt = "Build a self-contained single HTML file real-time 3D metaball renderer on an HTML5 Canvas using vanilla JavaScript. Use the marching-cubes algorithm on a 32³ grid to extract an isosurface from 5 animated metaball potentials. Render the resulting triangle mesh with Gouraud shading and a single point light. Animate the metaballs orbiting and merging in sinusoidal paths. Add mouse-drag orbit camera control and scroll-to-zoom. Target 20+ FPS. No WebGL framework — manual projection and triangle fill."
        },
        new PromptTemplate
        {
            Id = "glsl-fractal",
            Name = "Shader Fractal",
            Category = "3D Shader",
            Icon = "🔮",
            Prompt = "Create a self-contained single HTML file that runs a real-time GLSL fragment shader via a WebGL canvas. Render an animated Mandelbulb 3D fractal using ray marching with a signed-distance function. Implement orbit-trap colouring, ambient occlusion, and soft shadows. Animate a slow camera path circling the fractal. Add mouse-drag to orbit and scroll to zoom. Include a fullscreen button. Use only raw WebGL (no Three.js or other wrappers) with the GLSL shader inlined as a template literal string."
        },
        new PromptTemplate
        {
            Id = "pbr-sphere",
            Name = "PBR Material Demo",
            Category = "3D Shader",
            Icon = "🌑",
            Prompt = "Build a self-contained single HTML file PBR (Physically Based Rendering) material showcase using raw WebGL and inlined GLSL shaders. Render a 3×3 grid of spheres with varying roughness (0→1 left-to-right) and metalness (0→1 top-to-bottom). Implement a Cook-Torrance BRDF with GGX NDF, Smith masking, and Fresnel-Schlick. Use an environment IBL approximation (a procedural sky gradient sampled as a hemisphere) for image-based lighting. Add a rotating point light. Allow mouse-drag to orbit the camera. No Three.js — raw WebGL only."
        },
        new PromptTemplate
        {
            Id = "fluid-shader",
            Name = "Fluid Simulation",
            Category = "3D Shader",
            Icon = "🌊",
            Prompt = "Create a self-contained single HTML file GPU fluid simulation using raw WebGL ping-pong framebuffers and GLSL shaders. Implement a 2D Navier-Stokes solver: advection, pressure projection, and vorticity confinement passes each as a GLSL fragment shader. Render the velocity field as a vivid dye colour map. Let the user click and drag to inject velocity and dye. Simulate at 512×512 resolution. Include a reset button and a toggle to show pressure vs. velocity vs. vorticity. No external libraries — raw WebGL and inlined GLSL only."
        },
        new PromptTemplate
        {
            Id = "voronoi-shader",
            Name = "Voronoi Crystal",
            Category = "3D Shader",
            Icon = "💠",
            Prompt = "Build a self-contained single HTML file WebGL demo that renders an animated 3D crystalline Voronoi structure as a GLSL ray-marched scene. Generate a field of Voronoi cells in 3D space; render the cell boundaries as glowing neon edges with refraction inside each cell. Animate the cell seed points drifting slowly. Implement reflections on cell faces and depth-fog. Mouse-drag orbits the camera; scroll zooms. Fullscreen toggle button. Inline all GLSL as template literals — raw WebGL, no Three.js."
        }
    };

    public IReadOnlyList<PromptTemplate> GetAll() => _templates.AsReadOnly();
    
    public IEnumerable<string> GetCategories() => _templates.Select(t => t.Category).Distinct();
    
    public IEnumerable<PromptTemplate> GetByCategory(string category) => 
        _templates.Where(t => t.Category == category);
    
    public PromptTemplate? GetById(string id) => _templates.FirstOrDefault(t => t.Id == id);
    
    public IEnumerable<PromptTemplate> Search(string query) =>
        _templates.Where(t => 
            t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            t.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            t.Prompt.Contains(query, StringComparison.OrdinalIgnoreCase));
}

public record PromptTemplate
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string Icon { get; init; }
    public required string Prompt { get; init; }
}