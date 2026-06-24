import sys

filepath = '/Users/chenmeijun/Documents/3dc/3dc/Assets/Scripts/Cutting/WorkpieceVoxel.cs'
code = open(filepath).read()
new_code = code.replace('DllImport("sdf_island_remover")', 'DllImport("sdf_island_remover_v2")')
open(filepath, 'w').write(new_code)
