import UnityPy
import json

path = "X:/Escape from Tarkov Arena/EscapeFromTarkovArena_Data/globalgamemanagers"
env = UnityPy.load(path)
print("version reported:", env.file.version if hasattr(env, "file") else "?")
for obj in env.objects:
    print(obj.path_id, obj.type.name, obj.byte_size)
