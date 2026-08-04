shader_type canvas_item;

void fragment() {
	float t = (UV.x + UV.y) * 0.5;
	COLOR = vec4(mix(vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), t), 1.0);
}
