// The mark on the front page, in pieces: the tile and its three glass bars, each a little
// askew and at a depth of its own, turning slowly, and each of them something to take hold
// of. A piece that is pulled goes a little way and comes back.
//
// It is the model the app for iPhone, iPad and Mac shows on its first run, and behaves as
// it does there (src/Solo.iOS, Stage.swift and GlassLens.swift): the same springs with the
// same numbers, and the same glass. The glass is the icon's own (Icons/Solo.icon): each
// bar's fade of colour and how much of it there is, and a lens that shows what is behind
// its middle larger and draws in what is just beyond its rim. three.js has a material that
// refracts, but a piece of it does not see another piece of it, and these bars overlap; so
// the bars are drawn here, back to front, each looking at a picture of what is behind it.
//
// The pieces are solid to each other, as they are in the app: a bar pulled into the next
// one leans on it and moves it, where a position set outright would pass through. That is
// cannon-es, a small physics library with nothing in it but JavaScript; the springs only
// say how hard to push, and it does the pushing and stops one piece at another.
//
// The page works without any of this: the slot holds the icon as a picture, and the scene
// takes its place only once it has drawn.

import {
    Color, DirectionalLight, Group, LinearFilter, MeshStandardMaterial, NoBlending, NoToneMapping, PerspectiveCamera,
    PMREMGenerator, Quaternion, Raycaster, Scene, ShaderMaterial, SRGBColorSpace, Vector2, Vector3, WebGLRenderTarget,
    WebGLRenderer,
} from 'three';
import { Body, Box, Vec3, World } from 'cannon-es';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

// --- The numbers the app uses ------------------------------------------------------------

/// The farthest a piece is pulled, however far the pointer goes, in the model's units.
const reach = 0.2;
/// How far back the camera is, as on the app's stage.
const distance = 5;
/// The icon's refraction: how far in from a bar's edge it reaches, and how strong it is.
const lens = { depth: 0.08515625, strength: 0.34546875, edgeLight: 0.1 };
/// How round a bar's corners are.
const corner = 0.089;
/// Back to front, which is the order they are drawn in: each sees the ones before it.
const bars = ['BarBack', 'BarMiddle', 'BarFront'];
const pieces = ['Tile', ...bars];

/// The icon's colours for a scheme: the tile's grey, the bars' fade from top to bottom, and
/// how much of each bar there is, back to front.
const palettes = {
    dark: { tile: 0.15, top: [1, 0.90636, 0], bottom: [0.40943, 0.38161, 0], opacity: [0.8, 0.6, 0.3] },
    light: { tile: 0.97, top: [0.94039, 0.72495, 0.49502], bottom: [1, 0.44275, 0], opacity: [0.8, 0.6, 0.2] },
};

// --- Glass -------------------------------------------------------------------------------

const glassVertex = /* glsl */`
    varying vec3 vLocal;
    varying vec3 vNormal;
    varying vec3 vView;

    void main() {
        vLocal = position;
        vNormal = normalMatrix * normal;
        vec4 view = modelViewMatrix * vec4(position, 1.0);
        vView = view.xyz;
        gl_Position = projectionMatrix * view;
    }
`;

const glassFragment = /* glsl */`
    // three.js gives these two to the vertex shader only. Asked for here by name, they
    // are the same ones: where a point of the glass is on screen is worked out twice,
    // once for where it is and once for where it looks.
    uniform mat4 projectionMatrix;
    uniform mat4 modelViewMatrix;
    uniform sampler2D uBehind;
    uniform vec3 uTop;
    uniform vec3 uBottom;
    uniform float uOpacity;
    uniform vec2 uHalf;
    uniform float uCorner;
    uniform float uDepth;
    uniform float uStrength;
    uniform float uEdgeLight;
    uniform vec3 uLight;

    varying vec3 vLocal;
    varying vec3 vNormal;
    varying vec3 vView;

    // How far a point is from the edge of a rounded rectangle about the origin: less than
    // nothing inside it.
    float edge(vec2 p, vec2 extent, float radius) {
        vec2 q = abs(p) - extent + radius;
        return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
    }

    // Which way is straight out of it from there.
    vec2 outward(vec2 p, vec2 extent, float radius) {
        vec2 q = abs(p) - extent + radius;
        vec2 side = vec2(p.x < 0.0 ? -1.0 : 1.0, p.y < 0.0 ? -1.0 : 1.0);
        if (q.x > 0.0 && q.y > 0.0) { return side * normalize(q); }
        return q.x > q.y ? vec2(side.x, 0.0) : vec2(0.0, side.y);
    }

    void main() {
        vec2 p = vLocal.xy;
        float inside = max(-edge(p, uHalf, uCorner), 0.0);
        float near = 1.0 - clamp(inside / uDepth, 0.0, 1.0);
        float pull = near * near;

        // The glass's own colour: a fade up its length, lighter at the rim.
        float along = clamp(p.y / (2.0 * uHalf.y) + 0.5, 0.0, 1.0);
        vec3 tint = mix(uBottom, uTop, along) + pull * pull * uEdgeLight;

        // Through its middle the glass shows things larger, from nearer its centre; at its
        // rim it shows what is just beyond the rim, drawn in.
        vec2 bent = p - p * uStrength * (1.0 - pull) + outward(p, uHalf, uCorner) * pull * uDepth;
        vec4 clip = projectionMatrix * modelViewMatrix * vec4(bent, 0.0, 1.0);
        vec4 behind = texture2D(uBehind, clip.xy / clip.w * 0.5 + 0.5);

        // What is behind is there with nothing in it too, where the page shows through: the
        // glass is laid over that as over anything, colour and cover both.
        vec4 colour = vec4(mix(behind.rgb, tint, uOpacity), mix(behind.a, 1.0, uOpacity));

        // And it shines: a glint where the light is mirrored to the eye, and a sheen where
        // the glass turns away from it.
        vec3 normal = normalize(vNormal);
        vec3 eyeward = normalize(-vView);
        float glint = pow(max(dot(normal, normalize(uLight + eyeward)), 0.0), 220.0) * 0.35;
        float sheen = pow(1.0 - max(dot(normal, eyeward), 0.0), 3.0) * 0.22;
        float shine = glint + sheen;
        colour.rgb += shine * (1.0 - colour.rgb);
        colour.a += shine * (1.0 - colour.a);

        gl_FragColor = colour;
        #include <tonemapping_fragment>
        #include <colorspace_fragment>
    }
`;

// --- Motion ------------------------------------------------------------------------------

/// Where a drag of this length would have a piece: it follows the pointer at first and
/// then less and less, never past its reach, and comes a little toward the eye.
function pull(dragX, dragY) {
    const length = Math.hypot(dragX, dragY);
    if (length < 0.0001) return new Vector3();
    const eased = reach * (1 - Math.exp(-length / reach));
    return new Vector3(dragX / length * eased, dragY / length * eased, eased * 0.3);
}

const xAxis = new Vector3(1, 0, 0), yAxis = new Vector3(0, 1, 0);

class Piece {
    /// A piece, and the body that stands for it among the others: a box the size of the
    /// piece, with no weight to pull it anywhere, in the stage's own space, so the pieces
    /// go with the stage as it turns.
    constructor(mesh, phase, world) {
        this.mesh = mesh;
        this.home = mesh.position.clone();
        this.facing = mesh.quaternion.clone();
        this.held = null;
        this.phase = phase;
        this.clock = 0;

        const box = mesh.geometry.boundingBox;
        const half = new Vec3((box.max.x - box.min.x) / 2, (box.max.y - box.min.y) / 2, (box.max.z - box.min.z) / 2);
        this.body = new Body({ mass: 1, shape: new Box(half), linearDamping: 0, angularDamping: 0, allowSleep: false });
        this.body.position.set(this.home.x, this.home.y, this.home.z);
        this.body.quaternion.set(this.facing.x, this.facing.y, this.facing.z, this.facing.w);
        world.addBody(this.body);
    }

    /// One step of its spring: pushed toward where it should be and turned toward how it
    /// should face, harder and with less give while it is in hand. Only its speed is
    /// changed here. Where that takes it is the world's to say, which knows what is in the way.
    push(dt, still) {
        this.clock += dt;
        const body = this.body;
        const position = new Vector3(body.position.x, body.position.y, body.position.z);
        const velocity = new Vector3(body.velocity.x, body.velocity.y, body.velocity.z);
        const drift = still ? 0 : 0.006 * Math.sin(this.clock * 0.9 + this.phase);
        const target = this.home.clone().add(this.held ?? new Vector3()).add(new Vector3(0, drift, 0));

        const stiffness = this.held ? 300 : 150, damping = this.held ? 34 : 9;
        velocity.addScaledVector(target.sub(position).multiplyScalar(stiffness).addScaledVector(velocity, -damping), dt);
        body.velocity.set(velocity.x, velocity.y, velocity.z);

        // Off to one side it turns to face where it has gone, as a card on a string would.
        // It leans by where it is and not by where it was asked to be, so a piece that was
        // only pushed there leans too.
        const offset = position.sub(this.home).clampScalar(-reach, reach);
        const aim = new Quaternion().setFromAxisAngle(yAxis, offset.x * 2.2)
            .multiply(new Quaternion().setFromAxisAngle(xAxis, -offset.y * 2.2))
            .multiply(this.facing);
        const turned = new Quaternion(body.quaternion.x, body.quaternion.y, body.quaternion.z, body.quaternion.w);
        const error = aim.multiply(turned.invert());
        if (error.w < 0) error.set(-error.x, -error.y, -error.z, -error.w);
        const axis = new Vector3(error.x, error.y, error.z), sine = axis.length();
        const spin = new Vector3(body.angularVelocity.x, body.angularVelocity.y, body.angularVelocity.z);
        const twist = spin.clone().multiplyScalar(-14);
        if (sine > 0.00001) twist.addScaledVector(axis, 120 * 2 * Math.atan2(sine, error.w) / sine);
        spin.addScaledVector(twist, dt);
        body.angularVelocity.set(spin.x, spin.y, spin.z);
    }

    /// The piece drawn where its body has got to.
    settle() {
        const body = this.body;
        this.mesh.position.set(body.position.x, body.position.y, body.position.z);
        this.mesh.quaternion.set(body.quaternion.x, body.quaternion.y, body.quaternion.z, body.quaternion.w);
    }

    /// Whether it is anywhere but at rest.
    get moving() {
        return this.held !== null || this.body.velocity.lengthSquared() > 0.000001
            || this.body.angularVelocity.lengthSquared() > 0.000001
            || this.mesh.position.distanceToSquared(this.home) > 0.0000001;
    }
}

// --- The scene ---------------------------------------------------------------------------

async function mount(host) {
    const canvas = document.createElement('canvas');
    const renderer = new WebGLRenderer({ canvas, alpha: true, antialias: true, powerPreference: 'low-power' });
    renderer.setClearColor(0x000000, 0);
    renderer.outputColorSpace = SRGBColorSpace;
    // The colours are the icon's, to be shown as they are and not graded.
    renderer.toneMapping = NoToneMapping;

    const scene = new Scene();
    const room = new PMREMGenerator(renderer);
    scene.environment = room.fromScene(new RoomEnvironment(), 0.04).texture;
    // The room is a bright one, and the tile is meant to be nearly black in the dark
    // scheme: turned down until the tile is its own grey with a little light on it.
    scene.environmentIntensity = 0.45;
    room.dispose();
    const key = new DirectionalLight(0xffffff, 1.1);
    key.position.set(-1.5, 2.5, 4);
    scene.add(key);
    const light = key.position.clone().normalize();

    const camera = new PerspectiveCamera(30, 1, 0.1, 40);
    const stage = new Group();
    scene.add(stage);

    // Where the pieces are solid: nothing falls in it, and nothing bounces.
    const world = new World({ gravity: new Vec3(0, 0, 0) });
    world.defaultContactMaterial.restitution = 0;
    world.defaultContactMaterial.friction = 0.2;

    const model = (await new GLTFLoader().loadAsync(host.dataset.model)).scene;
    const tileMaterial = new MeshStandardMaterial({ roughness: 0.32, metalness: 0 });
    const parts = pieces.map((name, index) => {
        const mesh = model.getObjectByName(name);
        if (!mesh?.isMesh) throw new Error(`The model has no ${name}.`);
        mesh.geometry.computeBoundingBox();
        return new Piece(mesh, index * 1.7, world);
    });
    parts.forEach(part => stage.add(part.mesh));
    parts[0].mesh.material = tileMaterial;

    // Each bar's glass, and the picture of what is behind it that it looks through.
    const glass = bars.map(name => {
        const mesh = parts[pieces.indexOf(name)].mesh;
        const box = mesh.geometry.boundingBox;
        mesh.material = new ShaderMaterial({
            vertexShader: glassVertex,
            fragmentShader: glassFragment,
            blending: NoBlending,
            uniforms: {
                uBehind: { value: null },
                uTop: { value: new Color() },
                uBottom: { value: new Color() },
                uOpacity: { value: 1 },
                uHalf: { value: new Vector2((box.max.x - box.min.x) / 2, (box.max.y - box.min.y) / 2) },
                uCorner: { value: corner },
                uDepth: { value: lens.depth },
                uStrength: { value: lens.strength },
                uEdgeLight: { value: lens.edgeLight },
                uLight: { value: light },
            },
        });
        // Smoothed as the canvas is. The glass shows what is behind it larger than life, so an
        // edge left jagged in this picture is seen through it more jagged still.
        const behind = new WebGLRenderTarget(1, 1, { minFilter: LinearFilter, magFilter: LinearFilter, depthBuffer: true, samples: 4 });
        return { mesh, behind };
    });

    // --- Colours, which are the reader's scheme's ---

    const scheme = matchMedia('(prefers-color-scheme: dark)');
    function paint() {
        const chosen = document.documentElement.dataset.colorScheme;
        const palette = palettes[chosen === 'light' || chosen === 'dark' ? chosen : scheme.matches ? 'dark' : 'light'];
        tileMaterial.color.setRGB(palette.tile, palette.tile, palette.tile, SRGBColorSpace);
        glass.forEach(({ mesh }, index) => {
            const uniforms = mesh.material.uniforms;
            uniforms.uTop.value.setRGB(...palette.top, SRGBColorSpace);
            uniforms.uBottom.value.setRGB(...palette.bottom, SRGBColorSpace);
            uniforms.uOpacity.value = palette.opacity[index];
        });
        wake();
    }

    // --- Size ---

    /// How much has to be in view: the mark, with room for a piece to be pulled to its
    /// reach and for the whole to turn. The tile is one unit square about (0, 0.2).
    const extent = { width: 1.5, height: 1.5, middle: new Vector3(0, 0.2, 0.05) };
    let pointsPerUnit = 1;
    function fit() {
        const width = Math.max(host.clientWidth, 1), height = Math.max(host.clientHeight, 1);
        renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 2));
        renderer.setSize(width, height, false);
        const buffer = renderer.getDrawingBufferSize(new Vector2());
        glass.forEach(({ behind }) => behind.setSize(buffer.x, buffer.y));

        // As tall as the mark, or as tall as shows all its width, whichever is more.
        const visible = Math.max(extent.height, extent.width * height / width);
        camera.aspect = width / height;
        camera.fov = 2 * Math.atan(visible / (2 * distance)) * 180 / Math.PI;
        camera.position.set(extent.middle.x, extent.middle.y, distance);
        camera.lookAt(extent.middle.x, extent.middle.y, 0);
        camera.updateProjectionMatrix();
        pointsPerUnit = height / visible;
        wake();
    }

    // --- Drawing ---

    function draw() {
        // Back to front. Each bar is left out of the picture it looks through, and in
        // every one after it.
        glass.forEach(({ mesh }) => { mesh.visible = false; });
        for (const { mesh, behind } of glass) {
            renderer.setRenderTarget(behind);
            renderer.clear();
            renderer.render(scene, camera);
            mesh.material.uniforms.uBehind.value = behind.texture;
            mesh.visible = true;
        }
        renderer.setRenderTarget(null);
        renderer.render(scene, camera);
    }

    // --- Time ---

    const calm = matchMedia('(prefers-reduced-motion: reduce)');
    let clock = 0, last = 0, running = false, inView = true;

    function advance(dt) {
        clock += dt;
        const still = calm.matches;
        // The whole mark turns a little from side to side as it idles.
        stage.rotation.set(
            still ? 0 : 3.5 * Math.PI / 180 * Math.sin(clock * 0.41 + 1),
            still ? 0 : 7 * Math.PI / 180 * Math.sin(clock * 0.55), 0, 'YXZ');
        parts.forEach(part => part.push(dt, still));
        world.step(dt);
        parts.forEach(part => part.settle());
    }

    function frame(now) {
        running = false;
        // A frame after a stall is not a second of motion.
        const dt = Math.min((now - last) / 1000, 1 / 30);
        last = now;
        // In steps no longer than a display's frame, which is what the springs were tuned at.
        for (let left = dt; left > 0.0001; left -= 1 / 120) advance(Math.min(left, 1 / 120));
        draw();
        // Someone who asked for less motion gets none that they didn't start: the scene
        // is drawn while a piece is in hand or on its way back, and then stands still.
        if (inView && !document.hidden && (!calm.matches || parts.some(part => part.moving))) wake();
    }

    function wake() {
        if (running) return;
        running = true;
        requestAnimationFrame(now => {
            if (last === 0 || now - last > 250) last = now - 1000 / 60;
            frame(now);
        });
    }

    // --- Hands ---

    const ray = new Raycaster();
    let inHand = null;

    function pointOf(event) {
        const box = canvas.getBoundingClientRect();
        return new Vector2((event.clientX - box.left) / box.width * 2 - 1, 1 - (event.clientY - box.top) / box.height * 2);
    }

    canvas.addEventListener('pointerdown', event => {
        if (inHand || (event.pointerType === 'mouse' && event.button !== 0)) return;
        ray.setFromCamera(pointOf(event), camera);
        const hit = ray.intersectObjects(parts.map(part => part.mesh), false)[0];
        if (!hit) return;
        const part = parts.find(candidate => candidate.mesh === hit.object);
        inHand = { part, id: event.pointerId, x: event.clientX, y: event.clientY };
        part.held = new Vector3();
        // So that it is still in hand when the pointer leaves the canvas. A pointer the
        // browser can't hold on to is followed for as long as it stays over the mark.
        try { canvas.setPointerCapture(event.pointerId); } catch { /* not a pointer to capture */ }
        canvas.classList.add('is-held');
        event.preventDefault();
        wake();
    });

    canvas.addEventListener('pointermove', event => {
        if (!inHand || event.pointerId !== inHand.id) {
            // Something to take hold of looks like it.
            if (!inHand && event.pointerType === 'mouse') {
                ray.setFromCamera(pointOf(event), camera);
                canvas.classList.toggle('is-over', ray.intersectObjects(parts.map(part => part.mesh), false).length > 0);
            }
            return;
        }
        inHand.part.held = pull((event.clientX - inHand.x) / pointsPerUnit, (inHand.y - event.clientY) / pointsPerUnit);
        wake();
    });

    function letGo(event) {
        if (!inHand || event.pointerId !== inHand.id) return;
        inHand.part.held = null;
        inHand = null;
        canvas.classList.remove('is-held');
        wake();
    }
    canvas.addEventListener('pointerup', letGo);
    canvas.addEventListener('pointercancel', letGo);
    canvas.addEventListener('lostpointercapture', letGo);

    // --- Where it stands in the page ---

    new ResizeObserver(fit).observe(host);
    new IntersectionObserver(entries => {
        inView = entries[0].isIntersecting;
        if (inView) wake();
    }).observe(host);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) wake(); });
    scheme.addEventListener('change', paint);
    calm.addEventListener('change', wake);
    new MutationObserver(paint).observe(document.documentElement, { attributes: true, attributeFilter: ['data-color-scheme'] });

    // Lost and found again by the browser, it is drawn afresh.
    canvas.addEventListener('webglcontextrestored', wake);

    host.append(canvas);
    paint();
    fit();
    // One frame drawn before it is shown: the picture it replaces stays until then.
    advance(1 / 120);
    draw();
    host.classList.add('is-live');

    // For looking at it where frames don't come by themselves: moves it on and draws it.
    host.heroLogo = {
        step(seconds = 1 / 60) {
            for (let left = seconds; left > 0.0001; left -= 1 / 120) advance(Math.min(left, 1 / 120));
            draw();
        },
        parts,
        world,
    };
}

for (const host of document.querySelectorAll('[data-hero-logo]')) {
    // Without it the page has the picture it came with, which is the same mark.
    mount(host).catch(error => console.warn('The front page\'s 3D mark could not be shown.', error));
}
