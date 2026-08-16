import axios from "axios";

// The one shared Axios instance — all API classes import this; nobody calls axios.create elsewhere.
// Interceptors and shared config belong here.
export default axios.create({
  baseURL: "/",
});
