import { defineConfig } from 'vite';
export default defineConfig({ base:'/phone/', build:{target:'es2022',sourcemap:false}, server:{host:'127.0.0.1'} });
